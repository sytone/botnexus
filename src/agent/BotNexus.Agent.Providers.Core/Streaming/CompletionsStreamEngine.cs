using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Diagnostics;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// Shared streaming engine for the OpenAI and Copilot Chat Completions providers.
/// <para>
/// The two Chat Completions providers were ~97% character-identical god-classes. Their only genuine
/// behavioral deltas are captured by <see cref="CompletionsTransportProfile"/>:
/// (a) always-on vs conditional dynamic-header decoration,
/// (b) Copilot response-header / usage telemetry hooks,
/// (c) Copilot's stricter HTTP error projection.
/// Everything else — the request loop, usage parsing, stop-reason mapping, error projection, tool
/// conversion, and the error/abort emit shapes — lives here once (step 6/6 of #1377). Each provider
/// collapses to a thin shell that constructs a profile and calls <see cref="StreamAsync"/>.
/// </para>
/// <para>
/// Copilot-only types (<c>CopilotInteractionId</c>, <c>CopilotResponseHeaders</c>,
/// <c>CopilotUsageActivity</c>) deliberately do not exist in Providers.Core, so the Copilot transport
/// supplies them as the profile's header/telemetry delegates rather than via a project reference.
/// </para>
/// </summary>
public static class CompletionsStreamEngine
{
    private static readonly OpenAIStreamProcessor StreamProcessor = new();
    // Total / per-frame caps for the untrusted SSE success body. Mirrors the merged Copilot guard
    // (#1668) so all engines agree on a legitimate body size; bounds an unbounded body or a single
    // never-terminating data: line before it can exhaust memory (#1685). 16 MiB total / 8 MiB frame.
    private const long MaxResponseBytes = BoundedHttpContent.DefaultMaxResponseBytes;
    private const long MaxFrameBytes = 8L * 1024 * 1024;
    // Error bodies are far smaller than success streams; cap them tighter so a hostile non-2xx body
    // cannot be buffered into a string before the throw.
    private const long ErrorBodyLimitBytes = 64L * 1024;

    /// <summary>
    /// Builds the canonical Chat Completions <see cref="LlmStream"/> for a provider: starts the
    /// activity span, runs the request loop on a background task, and routes cancellation/errors to
    /// the shared abort/error emit shapes. Mirrors the (now-removed) per-provider <c>Stream</c> body.
    /// </summary>
    public static LlmStream StreamAsync(
        CompletionsTransportProfile profile,
        HttpClient httpClient,
        ILogger logger,
        LlmModel model,
        Context context,
        StreamOptions? options)
    {
        var stream = new LlmStream();
        var ct = options?.CancellationToken ?? CancellationToken.None;

        _ = Task.Run(async () =>
        {
            using var activity = ProviderDiagnostics.Source.StartActivity(profile.ActivityName, ActivityKind.Client);
            activity?.SetTag("botnexus.provider.name", model.Provider);
            activity?.SetTag("botnexus.model", model.Id);
            activity?.SetTag("botnexus.model.api", model.Api);

            try
            {
                await StreamCoreAsync(profile, httpClient, logger, stream, model, context, options, ct);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (OperationCanceledException)
            {
                EmitAborted(stream, profile.Api, model);
                activity?.SetStatus(ActivityStatusCode.Error, "Operation canceled");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Api} stream failed for model {Model}", profile.Api, model.Id);
                EmitError(stream, profile.Api, model, ex.Message);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            }
        }, ct);

        return stream;
    }

    private static async Task StreamCoreAsync(
        CompletionsTransportProfile profile,
        HttpClient httpClient,
        ILogger logger,
        LlmStream stream,
        LlmModel model,
        Context context,
        StreamOptions? options,
        CancellationToken ct)
    {
        var compat = CompatResolver.Resolve(model);
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"No API key for {model.Provider}. Set credentials before using model '{model.Id}'.");
        }

        var messages = MessageTransformer.TransformMessages(context.Messages, model);

        var payload = profile.BuildPayload(model, context.SystemPrompt, messages, context.Tools, options, compat);

        if (options?.OnPayload is not null)
        {
            var modified = await options.OnPayload(payload, model);
            if (modified is JsonObject obj)
                payload = obj;
        }

        var url = $"{model.BaseUrl.TrimEnd('/')}/chat/completions";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (model.Headers is not null)
        {
            foreach (var (key, value) in model.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        if (options?.Headers is not null)
        {
            foreach (var (key, value) in options.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        profile.DecorateHeaders(request, model, messages, options);

        logger.LogDebug("Streaming {Model} from {Url}", model.Id, url);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        profile.OnResponseHeaders?.Invoke(response);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody;
            try
            {
                errorBody = await BoundedHttpContent.ReadStringWithLimitAsync(
                    response.Content, ErrorBodyLimitBytes, ct);
            }
            catch (ResponseContentTooLargeException)
            {
                errorBody = $"<error body exceeded {ErrorBodyLimitBytes} bytes and was discarded>";
            }
            var providerError = ExtractProviderErrorMessage(errorBody, model);
            profile.ThrowForError(response, providerError);
        }

        using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        // Bound the untrusted SSE body before a byte reaches the parser: every byte the StreamReader
        // consumes flows through the ByteCountingStream, so an unbounded body or a never-terminating
        // data: line trips the cap regardless of buffering (#1685). Caller owns the inner stream.
        using var boundedStream = new ByteCountingStream(responseStream, MaxResponseBytes, MaxFrameBytes, leaveOpen: true);
        using var reader = new StreamReader(boundedStream, Encoding.UTF8);

        await StreamProcessor.ParseOpenAiCompletionsAsync(
            stream,
            reader,
            model,
            profile.Api,
            ParseUsage,
            MapStopReason,
            ExtractProviderErrorMessage,
            (s, m, msg, partial) => EmitError(s, profile.Api, m, msg, partial),
            () => logger.LogDebug("Skipping malformed SSE chunk"),
            ct,
            inspectChunk: profile.InspectChunk);
    }

    /// <summary>
    /// Converts the agent tool catalogue into the Chat Completions <c>tools</c> array. Honours the
    /// resolved compat profile's strict-mode capability.
    /// </summary>
    public static JsonArray ConvertTools(IReadOnlyList<Tool> tools, OpenAICompletionsCompat compat)
    {
        var result = new JsonArray();

        foreach (var tool in tools)
        {
            var fn = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = JsonNode.Parse(tool.Parameters.GetRawText())
            };

            if (compat.SupportsStrictMode != false)
                fn["strict"] = false;

            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = fn
            });
        }

        return result;
    }

    /// <summary>
    /// Projects a Chat Completions <c>usage</c> object into the core <see cref="Usage"/> model,
    /// folding reported cached/cache-write tokens out of the billed input count, adding reasoning
    /// tokens into output, and attaching computed cost.
    /// </summary>
    public static Usage ParseUsage(JsonElement usageElement, Usage usage, LlmModel model)
    {
        var promptTokens = usage.Input + usage.CacheRead + usage.CacheWrite;
        var completionTokens = usage.Output;
        var reportedCachedTokens = usage.CacheRead;
        var cacheWriteTokens = usage.CacheWrite;
        var reasoningTokens = 0;

        if (usageElement.TryGetProperty("prompt_tokens", out var pt))
            promptTokens = pt.GetInt32();

        if (usageElement.TryGetProperty("completion_tokens", out var ct))
            completionTokens = ct.GetInt32();

        if (usageElement.TryGetProperty("prompt_tokens_details", out var ptDetails) &&
            ptDetails.TryGetProperty("cached_tokens", out var cached))
        {
            reportedCachedTokens = cached.GetInt32();
        }

        if (usageElement.TryGetProperty("prompt_tokens_details", out ptDetails) &&
            ptDetails.TryGetProperty("cache_write_tokens", out var cacheWrite))
        {
            cacheWriteTokens = cacheWrite.GetInt32();
        }

        if (usageElement.TryGetProperty("completion_tokens_details", out var completionDetails) &&
            completionDetails.TryGetProperty("reasoning_tokens", out var reasoning))
        {
            reasoningTokens = reasoning.GetInt32();
        }

        var cacheReadTokens = cacheWriteTokens > 0
            ? Math.Max(0, reportedCachedTokens - cacheWriteTokens)
            : reportedCachedTokens;

        var inputTokens = Math.Max(0, promptTokens - cacheReadTokens - cacheWriteTokens);
        var outputTokens = completionTokens + reasoningTokens;

        var updated = usage with
        {
            Input = inputTokens,
            Output = outputTokens,
            CacheRead = cacheReadTokens,
            CacheWrite = cacheWriteTokens,
            TotalTokens = inputTokens + outputTokens + cacheReadTokens + cacheWriteTokens
        };
        updated = updated with { Cost = ModelRegistry.CalculateCost(model, updated) };
        return updated;
    }

    /// <summary>
    /// Maps a Chat Completions <c>finish_reason</c> to the core <see cref="StopReason"/> plus an
    /// optional human-readable error message for the failure-style reasons.
    /// </summary>
    public static (StopReason StopReason, string? ErrorMessage) MapStopReason(string? reason) => reason switch
    {
        "stop" => (StopReason.Stop, null),
        "end" => (StopReason.Stop, null),
        "length" => (StopReason.Length, null),
        "function_call" => (StopReason.ToolUse, null),
        "tool_calls" => (StopReason.ToolUse, null),
        "content_filter" => (StopReason.Error, "Content filtered by provider"),
        "refusal" => (StopReason.Refusal, null),
        "network_error" => (StopReason.Error, "Provider finish_reason: network_error"),
        null => (StopReason.Stop, null),
        _ => (StopReason.Error, $"Provider finish_reason: {reason}")
    };

    /// <summary>
    /// Maps a <see cref="ThinkingLevel"/> to the provider's reasoning-effort string, honouring a
    /// compat override map when one is present.
    /// </summary>
    public static string MapThinkingLevel(ThinkingLevel level, OpenAICompletionsCompat? compat)
    {
        if (compat?.ReasoningEffortMap is not null &&
            compat.ReasoningEffortMap.TryGetValue(level, out var mapped))
            return mapped;

        return level switch
        {
            ThinkingLevel.Minimal => "low",
            ThinkingLevel.Low => "low",
            ThinkingLevel.Medium => "medium",
            ThinkingLevel.High => "high",
            ThinkingLevel.ExtraHigh => "xhigh",
            ThinkingLevel.Max => "xhigh",
            _ => "medium"
        };
    }

    /// <summary>
    /// Extracts the most useful error message from a provider error body, with an OpenRouter-specific
    /// path that appends the metadata code / provider name when present.
    /// </summary>
    public static string ExtractProviderErrorMessage(string rawError, LlmModel model)
    {
        if (string.IsNullOrWhiteSpace(rawError))
            return "Unknown API error";

        try
        {
            using var doc = JsonDocument.Parse(rawError);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var messageEl)
                    ? messageEl.GetString()
                    : null;

                if (string.Equals(model.Provider, "openrouter", StringComparison.OrdinalIgnoreCase) &&
                    error.TryGetProperty("metadata", out var metadata) &&
                    metadata.ValueKind == JsonValueKind.Object)
                {
                    var code = metadata.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
                    var providerName = metadata.TryGetProperty("provider_name", out var providerEl) ? providerEl.GetString() : null;
                    var suffix = string.Join(", ", new[] { code, providerName }.Where(v => !string.IsNullOrWhiteSpace(v)));
                    if (!string.IsNullOrWhiteSpace(suffix))
                        return $"{message ?? "OpenRouter error"} ({suffix})";
                }

                return message ?? rawError;
            }
        }
        catch (JsonException)
        {
        }

        return rawError;
    }

    /// <summary>
    /// Pushes the canonical error event/end for a failed completion turn, optionally carrying any
    /// partial content already streamed.
    /// </summary>
    public static void EmitError(LlmStream stream, string api, LlmModel model, string errorMessage,
        List<ContentBlock>? partialContent = null)
    {
        var message = new AssistantMessage(
            Content: partialContent ?? [],
            Api: api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Error,
            ErrorMessage: errorMessage,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        stream.Push(new ErrorEvent(StopReason.Error, message));
        stream.End(message);
    }

    /// <summary>
    /// Pushes the canonical done event/end for a cancelled completion turn.
    /// </summary>
    public static void EmitAborted(LlmStream stream, string api, LlmModel model)
    {
        var message = new AssistantMessage(
            Content: [],
            Api: api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Aborted,
            ErrorMessage: "Request was cancelled",
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        stream.Push(new DoneEvent(StopReason.Aborted, message));
        stream.End(message);
    }
}

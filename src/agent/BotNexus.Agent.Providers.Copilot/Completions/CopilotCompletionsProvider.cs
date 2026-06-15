using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Diagnostics;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Copilot.Completions;

/// <summary>
/// GitHub Copilot Chat Completions API provider. Carved out of <see cref="BotNexus.Agent.Providers.OpenAI.OpenAICompletionsProvider"/> so the Copilot transport has no cross-provider dependency on the OpenAI project. Always applies Copilot dynamic headers.
/// See <c>tests/.../CopilotCompletionsProviderParityTests</c> for the byte-identical-body proof against the legacy OpenAI-with-Copilot-auth path.
/// Uses raw HttpClient for SSE streaming — full control over headers, compat, and streaming.
/// </summary>
public sealed class CopilotCompletionsProvider(
    HttpClient httpClient,
    ILogger<CopilotCompletionsProvider> logger) : IApiProvider
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private static readonly OpenAIStreamProcessor StreamProcessor = new();

    public string Api => "github-copilot-completions";

    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
    {
        var stream = new LlmStream();
        var ct = options?.CancellationToken ?? CancellationToken.None;

        _ = Task.Run(async () =>
        {
            using var activity = ProviderDiagnostics.Source.StartActivity("provider.copilot-completions.stream", ActivityKind.Client);
            activity?.SetTag("botnexus.provider.name", model.Provider);
            activity?.SetTag("botnexus.model", model.Id);
            activity?.SetTag("botnexus.model.api", model.Api);

            try
            {
                await StreamCoreAsync(stream, model, context, options, ct);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (OperationCanceledException)
            {
                EmitAborted(stream, model);
                activity?.SetStatus(ActivityStatusCode.Error, "Operation canceled");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Copilot completions stream failed for model {Model}", model.Id);
                EmitError(stream, model, ex.Message);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            }
        }, ct);

        return stream;
    }

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";

        var completionsOptions = new CopilotCompletionsOptions
        {
            ApiKey = apiKey,
            Temperature = options?.Temperature,
            MaxTokens = options?.MaxTokens,
            CancellationToken = options?.CancellationToken ?? CancellationToken.None,
            Transport = options?.Transport ?? Transport.Sse,
            CacheRetention = options?.CacheRetention ?? CacheRetention.Short,
            SessionId = options?.SessionId,
            OnPayload = options?.OnPayload,
            Headers = options?.Headers,
            MaxRetryDelayMs = options?.MaxRetryDelayMs ?? 60000,
            Metadata = options?.Metadata,
        };

        if (options?.Reasoning is not null && model.Reasoning)
            completionsOptions.ReasoningEffort = MapThinkingLevel(options.Reasoning.Value, CompatResolver.Resolve(model));

        return Stream(model, context, completionsOptions);
    }

    private async Task StreamCoreAsync(
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

        var payload = CopilotCompletionsRequestBuilder.Build(
            model, context.SystemPrompt, messages, context.Tools, options, compat,
            CopilotCompletionsMessageConverter.Convert, ConvertTools);

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

        // Copilot transport always applies the dynamic vision/intent headers —
        // this provider only handles Copilot-routed models, so the runtime check
        // present in the OpenAI parent is unnecessary.
        var copilotHasImages = CopilotHeaders.HasVisionInput(messages);
        var copilotHeaderOptions = Headers.CopilotInteractionId.WithResolvedInteractionId(
            (options as CopilotCompletionsOptions)?.HeaderOptions);
        foreach (var (key, value) in CopilotHeaders.BuildDynamicHeaders(messages, copilotHasImages, copilotHeaderOptions))
            request.Headers.TryAddWithoutValidation(key, value);

        logger.LogDebug("Streaming {Model} from {Url}", model.Id, url);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Headers.CopilotResponseHeaders.EmitToActivity(response, Activity.Current);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            var providerError = ExtractProviderErrorMessage(errorBody, model);
            ProviderHttpErrorHelper.ThrowForFailedResponse(response, providerError, "Copilot Completions");
        }

        using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(responseStream, Encoding.UTF8);

        await StreamProcessor.ParseOpenAiCompletionsAsync(
            stream,
            reader,
            model,
            Api,
            ParseUsage,
            MapStopReason,
            ExtractProviderErrorMessage,
            EmitError,
            () => logger.LogDebug("Skipping malformed SSE chunk"),
            ct,
            inspectChunk: root => Telemetry.CopilotUsageActivity.TryParseAndEmit(root, Activity.Current));
    }

    #region Tool Conversion

    private static JsonArray ConvertTools(IReadOnlyList<Tool> tools, OpenAICompletionsCompat compat)
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

    #endregion

    #region Usage & Mapping

    private static Usage ParseUsage(JsonElement usageElement, Usage usage, LlmModel model)
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

    private static (StopReason StopReason, string? ErrorMessage) MapStopReason(string? reason) => reason switch
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

    private static string MapThinkingLevel(ThinkingLevel level, OpenAICompletionsCompat? compat)
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
            _ => "medium"
        };
    }

    private static string ExtractProviderErrorMessage(string rawError, LlmModel model)
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

    #endregion

    #region Error Helpers

    private void EmitError(LlmStream stream, LlmModel model, string errorMessage,
        List<ContentBlock>? partialContent = null)
    {
        var message = new AssistantMessage(
            Content: partialContent ?? [],
            Api: Api,
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

    private void EmitAborted(LlmStream stream, LlmModel model)
    {
        var message = new AssistantMessage(
            Content: [],
            Api: Api,
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

    #endregion

}

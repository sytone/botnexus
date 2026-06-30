using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Diagnostics;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Copilot.Messages;

/// <summary>
/// GitHub Copilot Anthropic-Messages-compatible provider. Carved out of
/// <c>AnthropicProvider</c>'s <c>AuthMode.Copilot</c> branch so the Copilot
/// transport has no cross-provider dependency on the Anthropic project.
/// Always uses Bearer auth with the Copilot OAuth access token and applies
/// Copilot dynamic headers on every request.
/// </summary>
public sealed partial class CopilotMessagesProvider(HttpClient httpClient) : IApiProvider
{
    private const string ApiVersion = "2023-06-01";
    public const string ApiId = "github-copilot-messages";

    /// <summary>
    /// Byte cap for the untrusted error-response body (64 KiB). Error payloads are tiny in
    /// practice; bounding prevents a hostile/malfunctioning endpoint from streaming a huge body on
    /// the failure path. Mirrors OpenClaw's <c>COPILOT_ERROR_BODY_LIMIT_BYTES</c> (issue #1653).
    /// </summary>
    private const long ErrorBodyLimitBytes = 64L * 1024;

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public string Api => ApiId;

    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
    {
        var stream = new LlmStream();
        var ct = options?.CancellationToken ?? CancellationToken.None;

        _ = Task.Run(async () =>
        {
            var usage = Usage.Empty();
            string? responseId = null;
            var stopReason = StopReason.Stop;
            var contentBlocks = new List<ContentBlock>();
            using var activity = ProviderDiagnostics.Source.StartActivity("provider.copilot-messages.stream", ActivityKind.Client);
            activity?.SetTag("botnexus.provider.name", model.Provider);
            activity?.SetTag("botnexus.model", model.Id);
            activity?.SetTag("botnexus.model.api", model.Api);

            try
            {
                await StreamCoreAsync(model, context, options, stream,
                    contentBlocks, usage,
                    updatedUsage => usage = updatedUsage,
                    id => responseId = id,
                    reason => stopReason = reason, ct);

                var final = BuildMessage(model, contentBlocks, usage, stopReason, null, responseId);
                stream.Push(new DoneEvent(stopReason, final));
                stream.End(final);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                var msg = BuildMessage(model, contentBlocks, usage, StopReason.Aborted, null, responseId);
                stream.Push(new ErrorEvent(StopReason.Aborted, msg));
                stream.End(msg);
                activity?.SetStatus(ActivityStatusCode.Error, "Operation canceled");
            }
            catch (Exception ex)
            {
                var msg = BuildMessage(model, contentBlocks, usage, StopReason.Error, ex.Message, responseId);
                stream.Push(new ErrorEvent(StopReason.Error, msg));
                stream.End(msg);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            }
        }, ct);

        return stream;
    }

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";
        var baseOptions = SimpleOptionsHelper.BuildBaseOptions(model, options, apiKey);

        var copilotOpts = new CopilotMessagesOptions
        {
            Temperature = baseOptions.Temperature,
            MaxTokens = baseOptions.MaxTokens,
            CancellationToken = baseOptions.CancellationToken,
            ApiKey = baseOptions.ApiKey,
            Transport = baseOptions.Transport,
            CacheRetention = baseOptions.CacheRetention,
            SessionId = baseOptions.SessionId,
            OnPayload = baseOptions.OnPayload,
            Headers = baseOptions.Headers,
            MaxRetryDelayMs = baseOptions.MaxRetryDelayMs,
            Metadata = baseOptions.Metadata,
            StreamSetupTimeoutMs = baseOptions.StreamSetupTimeoutMs,
        };

        if (options?.Reasoning is { } reasoning)
        {
            var clamped = SimpleOptionsHelper.ClampReasoning(reasoning);

            if (IsAdaptiveThinkingModel(model.Id))
            {
                copilotOpts.ThinkingEnabled = true;
                var maxCapable = ModelRegistry.SupportsExtraHigh(model);
                copilotOpts.Effort = reasoning switch
                {
                    ThinkingLevel.Minimal => "low",
                    ThinkingLevel.Low => "low",
                    ThinkingLevel.Medium => "medium",
                    ThinkingLevel.High => "high",
                    ThinkingLevel.ExtraHigh => maxCapable ? "max" : "high",
                    ThinkingLevel.Max => maxCapable ? "max" : "high",
                    _ => "high"
                };
            }
            else if (model.Reasoning)
            {
                var budgetLevel = SimpleOptionsHelper.GetBudgetForLevel(
                    clamped ?? ThinkingLevel.Medium, options?.ThinkingBudgets);

                var maxTokens = copilotOpts.MaxTokens;
                var budgetTokens = budgetLevel ?? SimpleOptionsHelper.GetDefaultThinkingBudget(clamped ?? ThinkingLevel.Medium);

                var (adjustedMax, adjustedBudget) = SimpleOptionsHelper.AdjustMaxTokensForThinking(
                    model, maxTokens, budgetTokens);

                copilotOpts = copilotOpts with
                {
                    ThinkingEnabled = true,
                    ThinkingBudgetTokens = adjustedBudget,
                    MaxTokens = adjustedMax
                };
            }
        }
        else if (model.Reasoning)
        {
            copilotOpts.ThinkingEnabled = false;
        }

        return Stream(model, context, copilotOpts);
    }

    private async Task StreamCoreAsync(
        LlmModel model, Context context, StreamOptions? options,
        LlmStream stream, List<ContentBlock> contentBlocks, Usage initialUsage,
        Action<Usage> setUsage,
        Action<string?> setResponseId, Action<StopReason> setStopReason,
        CancellationToken ct)
    {
        var copilotOpts = options as CopilotMessagesOptions;
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"No API key for {model.Provider}. Set credentials before using model '{model.Id}'.");
        }

        var baseUrl = model.BaseUrl.TrimEnd('/');

        var requestBody = CopilotMessagesRequestBuilder.BuildRequestBody(
            model,
            context,
            options,
            copilotOpts,
            IsAdaptiveThinkingModel);

        if (options?.OnPayload is { } onPayload)
        {
            var modified = await onPayload(requestBody, model);
            if (modified is JsonObject modifiedObject)
                requestBody = modifiedObject;
        }

        var json = requestBody.ToJsonString();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages");
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
        ConfigureRequestHeaders(httpRequest, apiKey, copilotOpts, model);

        // Copilot transport always applies the dynamic vision/intent headers.
        var hasImages = CopilotHeaders.HasVisionInput(context.Messages);
        var headerOptions = Headers.CopilotInteractionId.WithResolvedInteractionId(copilotOpts?.HeaderOptions);
        foreach (var (key, value) in CopilotHeaders.BuildDynamicHeaders(context.Messages, hasImages, headerOptions))
            httpRequest.Headers.TryAddWithoutValidation(key, value);

        var setupTimeoutMs = options?.StreamSetupTimeoutMs ?? 0;
        using var setupTimeoutCts = setupTimeoutMs > 0
            ? new CancellationTokenSource(setupTimeoutMs)
            : null;
        using var linkedCts = setupTimeoutCts is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, setupTimeoutCts.Token)
            : null;
        var effectiveCt = linkedCts?.Token ?? ct;

        using var response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, effectiveCt);

        // Surface Copilot response correlation IDs + quota snapshots before the
        // success check so error responses are still observable.
        Headers.CopilotResponseHeaders.EmitToActivity(response, Activity.Current);

        if (!response.IsSuccessStatusCode)
        {
            // Bound the untrusted error body so a hostile/malfunctioning endpoint cannot stream a
            // huge body on the failure path (OOM-DoS guard, #1653). A truncation here must not mask
            // the real HTTP failure -- on over-cap, fall back to a short placeholder so
            // ThrowForFailedResponse still surfaces the status code.
            string errorBody;
            try
            {
                errorBody = await BoundedHttpContent.ReadStringWithLimitAsync(
                    response.Content, ErrorBodyLimitBytes, effectiveCt);
            }
            catch (ResponseContentTooLargeException)
            {
                errorBody = $"<error body exceeded {ErrorBodyLimitBytes} bytes and was discarded>";
            }

            ProviderHttpErrorHelper.ThrowForFailedResponse(response, errorBody, "Copilot Messages");
        }

        using var responseStream = await response.Content.ReadAsStreamAsync(effectiveCt);

        Action? onFirstToken = setupTimeoutCts is not null
            ? () =>
            {
                try { setupTimeoutCts.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            : null;

        // The parser owns the streaming byte guard (#1668): hand it the raw response stream and it
        // wraps it in a ByteCountingStream before reading, bounding the untrusted SSE body.
        var (usage, responseId, stopReason) = await CopilotMessagesStreamParser.ProcessStreamAsync(
            responseStream,
            model,
            stream,
            contentBlocks,
            initialUsage,
            BuildMessage,
            MapStopReason,
            ct,
            onFirstToken);

        setUsage(usage);
        setResponseId(responseId);
        setStopReason(stopReason);
    }

    private static void ConfigureRequestHeaders(
        HttpRequestMessage request, string apiKey,
        CopilotMessagesOptions? opts, LlmModel model)
    {
        request.Headers.Add("anthropic-version", ApiVersion);
        request.Headers.TryAddWithoutValidation("accept", "application/json");
        request.Headers.TryAddWithoutValidation("anthropic-dangerous-direct-browser-access", "true");

        if (model.Headers is not null)
        {
            foreach (var (key, value) in model.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        if (opts?.Headers is not null)
        {
            foreach (var (key, value) in opts.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        var betaFeatures = new List<string>();
        if (opts?.InterleavedThinking == true && !IsAdaptiveThinkingModel(model.Id))
            betaFeatures.Add("interleaved-thinking-2025-05-14");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        if (betaFeatures.Count > 0)
            request.Headers.TryAddWithoutValidation("anthropic-beta", string.Join(",", betaFeatures));
    }

    internal static AssistantMessage BuildMessage(
        LlmModel model, List<ContentBlock> content,
        Usage usage, StopReason stopReason, string? errorMessage, string? responseId)
    {
        var usageWithTotals = usage with
        {
            TotalTokens = usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite
        };
        usageWithTotals = usageWithTotals with
        {
            Cost = ModelRegistry.CalculateCost(model, usageWithTotals)
        };

        return new AssistantMessage(
            Content: [.. content],
            Api: ApiId,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: usageWithTotals,
            StopReason: stopReason,
            ErrorMessage: errorMessage,
            ResponseId: responseId,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static StopReason MapStopReason(string? reason) => reason switch
    {
        "end_turn" => StopReason.Stop,
        "max_tokens" => StopReason.Length,
        "tool_use" => StopReason.ToolUse,
        "refusal" => StopReason.Refusal,
        "pause_turn" => StopReason.Stop,
        "stop_sequence" => StopReason.Stop,
        "content_policy" => StopReason.Sensitive,
        "safety" => StopReason.Sensitive,
        "sensitive" => StopReason.Sensitive,
        _ => throw new InvalidOperationException($"Unhandled Copilot Messages stop reason: {reason}")
    };

    internal static bool IsAdaptiveThinkingModel(string modelId) =>
        modelId.Contains("opus-4-6", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("opus-4.6", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("opus-4-8", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("opus-4.8", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("sonnet-4-6", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("sonnet-4.6", StringComparison.OrdinalIgnoreCase);
}

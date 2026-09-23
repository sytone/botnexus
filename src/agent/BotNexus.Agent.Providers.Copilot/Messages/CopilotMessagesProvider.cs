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
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Agent.Providers.Copilot.Messages;

/// <summary>
/// GitHub Copilot Anthropic-Messages-compatible provider. Carved out of
/// <c>AnthropicProvider</c>'s <c>AuthMode.Copilot</c> branch so the Copilot
/// transport has no cross-provider dependency on the Anthropic project.
/// Always uses Bearer auth with the Copilot OAuth access token and applies
/// Copilot dynamic headers on every request.
/// </summary>
/// <param name="httpClient">The shared provider HTTP client.</param>
/// <param name="secretRedactor">
/// Optional secret redactor applied to a non-2xx error body before it is interpolated into an
/// exception message that the agent loop persists as the session-visible <c>ErrorMessage</c> (#2881).
/// </param>
public sealed partial class CopilotMessagesProvider(HttpClient httpClient, ISecretRedactor? secretRedactor = null) : IApiProvider
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
    private readonly CopilotEffortCapabilityCache _effortCapabilities = new();

    public string Api => ApiId;

    /// <summary>
    /// The Anthropic Messages wire protocol over the Copilot transport: system prompt in the
    /// dedicated top-level <c>system</c> field (see <c>CopilotMessagesRequestBuilder</c>), and
    /// leaked-tool-call recovery DECLARED -- this is the transport on which #1709 was observed
    /// ("opus via github-copilot" leaking <c>invoke</c>/<c>tool_use</c> XML into the assistant text
    /// channel with a non-ToolUse finish reason) (#2432).
    /// </summary>
    public ProviderCapabilities Capabilities { get; } = new(
        RecoversLeakedToolCallMarkup: true,
        SystemPromptPlacement: SystemPromptPlacement.DedicatedField);

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
        var credential = ProviderCredentialResolver.Resolve(model.Provider, options?.ApiKey, null);
        var apiKey = credential.Value;
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
            StreamIdleTimeoutMs = baseOptions.StreamIdleTimeoutMs,
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
        var credential = ProviderCredentialResolver.Resolve(model.Provider, options?.ApiKey, null);
        var apiKey = credential.Value;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"No API key for {model.Provider}. Set credentials before using model '{model.Id}'.");
        }

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

        var requestedEffort = requestBody["output_config"]?["effort"]?.GetValue<string>();
        if (requestedEffort is not null)
        {
            var clamped = _effortCapabilities.Clamp(model.Id, model.Name, requestedEffort);
            requestBody["output_config"]!["effort"] = clamped;
            requestedEffort = clamped;
        }

        var baseUrl = model.BaseUrl.TrimEnd('/');
        var setupTimeoutMs = options?.StreamSetupTimeoutMs ?? 0;
        using var setupTimeoutCts = setupTimeoutMs > 0
            ? new CancellationTokenSource(setupTimeoutMs)
            : null;
        using var linkedCts = setupTimeoutCts is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, setupTimeoutCts.Token)
            : null;
        var effectiveCt = linkedCts?.Token ?? ct;
        var hasImages = CopilotHeaders.HasVisionInput(context.Messages);
        var headerOptions = Headers.CopilotInteractionId.WithResolvedInteractionId(copilotOpts?.HeaderOptions);

        HttpResponseMessage? response = null;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var httpRequest = BuildHttpRequest(
                    requestBody, baseUrl, apiKey, copilotOpts, model, context, hasImages, headerOptions);
                response = await _httpClient.SendAsync(
                    httpRequest, HttpCompletionOption.ResponseHeadersRead, effectiveCt);

                Headers.CopilotResponseHeaders.EmitToActivity(response, Activity.Current);
                if (response.IsSuccessStatusCode)
                    break;

                var errorBody = await ReadErrorBodyAsync(response, effectiveCt);
                if (attempt == 0 &&
                    response.StatusCode == System.Net.HttpStatusCode.BadRequest &&
                    requestedEffort is not null &&
                    CopilotEffortCapabilityCache.TryParseRejection(
                        errorBody, requestedEffort, out var authoritativeModelId, out var supported) &&
                    CopilotEffortCapabilityCache.SelectClosest(requestedEffort, supported) is { } selected &&
                    !string.Equals(selected, requestedEffort, StringComparison.Ordinal))
                {
                    _effortCapabilities.Remember(authoritativeModelId, model.Id, supported);
                    _effortCapabilities.Remember(authoritativeModelId, model.Name, supported);
                    Activity.Current?.AddEvent(new ActivityEvent(
                        "copilot.messages.effort_fallback",
                        tags: new ActivityTagsCollection
                        {
                            ["botnexus.model"] = model.Id,
                            ["botnexus.copilot.authoritative_model"] = authoritativeModelId,
                            ["botnexus.copilot.requested_effort"] = requestedEffort,
                            ["botnexus.copilot.selected_effort"] = selected,
                        }));
                    requestBody["output_config"]!["effort"] = selected;
                    requestedEffort = selected;
                    response.Dispose();
                    response = null;
                    continue;
                }

                ProviderHttpErrorHelper.ThrowForFailedResponse(response, errorBody, "Copilot Messages", secretRedactor);
            }

            using var responseStream = await response.Content.ReadAsStreamAsync(effectiveCt);
            Action? onFirstToken = setupTimeoutCts is not null
                ? () =>
                {
                    try { setupTimeoutCts.Cancel(); }
                    catch (ObjectDisposedException) { }
                }
                : null;

            var (usage, responseId, stopReason) = await CopilotMessagesStreamParser.ProcessStreamAsync(
                responseStream,
                model,
                stream,
                contentBlocks,
                initialUsage,
                BuildMessage,
                MapStopReason,
                ct,
                onFirstToken,
                StreamIdleTimeout.Resolve(options));

            setUsage(usage);
            setResponseId(responseId);
            setStopReason(stopReason);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static HttpRequestMessage BuildHttpRequest(
        JsonObject requestBody,
        string baseUrl,
        string apiKey,
        CopilotMessagesOptions? copilotOpts,
        LlmModel model,
        Context context,
        bool hasImages,
        CopilotHeaderOptions? headerOptions)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages")
        {
            Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json")
        };
        ConfigureRequestHeaders(request, apiKey, copilotOpts, model);
        foreach (var (key, value) in CopilotHeaders.BuildDynamicHeaders(context.Messages, hasImages, headerOptions))
            request.Headers.TryAddWithoutValidation(key, value);
        return request;
    }

    private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await BoundedHttpContent.ReadStringWithLimitAsync(
                response.Content, ErrorBodyLimitBytes, cancellationToken);
        }
        catch (ResponseContentTooLargeException)
        {
            return $"<error body exceeded {ErrorBodyLimitBytes} bytes and was discarded>";
        }
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

    /// <summary>
    /// Maps a Copilot Messages <c>stop_reason</c> to the core <see cref="StopReason"/>. Delegates to
    /// the shared total mapper (#3564) rather than carrying a private switch whose default arm threw:
    /// an unrecognised or absent stop reason now degrades the turn instead of destroying it.
    /// </summary>
    internal static StopReason MapStopReason(string? reason) =>
        MessagesStopReasonMap.MapStopReason(reason, "Copilot Messages");

    // #2374: delegates to the shared parsed family+version gate rather than carrying a private copy
    // of the literal substring list. A new Opus/Sonnet generation now needs no edit here at all.
    internal static bool IsAdaptiveThinkingModel(string modelId) =>
        ModelCapabilityHeuristics.IsAdaptiveThinkingModel(modelId);
}

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Diagnostics;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.Core.Streaming;
using BotNexus.Providers.Core.Utilities;

namespace BotNexus.Providers.Anthropic;

/// <summary>
/// Anthropic Messages API provider. Port of pi-mono's providers/anthropic.ts.
/// Orchestrates SSE streaming, three auth modes, and thinking configuration.
/// Delegates request building to <see cref="AnthropicRequestBuilder"/>,
/// message conversion to <see cref="AnthropicMessageConverter"/>,
/// and stream parsing to <see cref="AnthropicStreamParser"/>.
/// </summary>
public sealed partial class AnthropicProvider(HttpClient httpClient) : IApiProvider
{
    private const string ApiVersion = "2023-06-01";
    private const string ClaudeCodeVersion = "2.1.75";
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public string Api => "anthropic-messages";

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
            using var activity = ProviderDiagnostics.Source.StartActivity("provider.anthropic.stream", ActivityKind.Client);
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

        var anthropicOpts = new AnthropicOptions
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
        };

        if (options?.Reasoning is { } reasoning)
        {
            var clamped = SimpleOptionsHelper.ClampReasoning(reasoning);

            if (IsAdaptiveThinkingModel(model.Id))
            {
                anthropicOpts.ThinkingEnabled = true;
                anthropicOpts.Effort = reasoning switch
                {
                    ThinkingLevel.Minimal => "low",
                    ThinkingLevel.Low => "low",
                    ThinkingLevel.Medium => "medium",
                    ThinkingLevel.High => "high",
                    ThinkingLevel.ExtraHigh => IsOpus46Model(model.Id) ? "max" : "high",
                    _ => "high"
                };
            }
            else if (model.Reasoning)
            {
                var budgetLevel = SimpleOptionsHelper.GetBudgetForLevel(
                    clamped ?? ThinkingLevel.Medium, options?.ThinkingBudgets);

                var maxTokens = anthropicOpts.MaxTokens;
                var budgetTokens = budgetLevel ?? SimpleOptionsHelper.GetDefaultThinkingBudget(clamped ?? ThinkingLevel.Medium);

                var (adjustedMax, adjustedBudget) = SimpleOptionsHelper.AdjustMaxTokensForThinking(
                    model, maxTokens, budgetTokens);

                anthropicOpts = anthropicOpts with
                {
                    ThinkingEnabled = true,
                    ThinkingBudgetTokens = adjustedBudget,
                    MaxTokens = adjustedMax
                };
            }
        }
        else if (model.Reasoning)
        {
            anthropicOpts.ThinkingEnabled = false;
        }

        return Stream(model, context, anthropicOpts);
    }

    private async Task StreamCoreAsync(
        LlmModel model, Context context, StreamOptions? options,
        LlmStream stream, List<ContentBlock> contentBlocks, Usage initialUsage,
        Action<Usage> setUsage,
        Action<string?> setResponseId, Action<StopReason> setStopReason,
        CancellationToken ct)
    {
        var anthropicOpts = options as AnthropicOptions;
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"No API key for {model.Provider}. Set credentials before using model '{model.Id}'.");
        }

        var baseUrl = model.BaseUrl.TrimEnd('/');
        var authMode = DetectAuthMode(apiKey, model);
        var isOAuthToken = authMode == AuthMode.OAuth;

        var requestBody = AnthropicRequestBuilder.BuildRequestBody(
            model,
            context,
            options,
            anthropicOpts,
            isOAuthToken,
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
        ConfigureRequestHeaders(httpRequest, apiKey, authMode, anthropicOpts, model);

        if (authMode == AuthMode.Copilot)
        {
            var hasImages = CopilotHeaders.HasVisionInput(context.Messages);
            foreach (var (key, value) in CopilotHeaders.BuildDynamicHeaders(context.Messages, hasImages))
                httpRequest.Headers.TryAddWithoutValidation(key, value);
        }

        using var response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Anthropic API returned {(int)response.StatusCode}: {errorBody}");
        }

        using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(responseStream, Encoding.UTF8);

        var (usage, responseId, stopReason) = await AnthropicStreamParser.ProcessStreamAsync(
            reader,
            model,
            stream,
            contentBlocks,
            initialUsage,
            context.Tools,
            isOAuthToken,
            BuildMessage,
            MapStopReason,
            ct);

        setUsage(usage);
        setResponseId(responseId);
        setStopReason(stopReason);
    }

    private enum AuthMode { ApiKey, OAuth, Copilot }

    private static AuthMode DetectAuthMode(string? apiKey, LlmModel model)
    {
        if (string.Equals(model.Provider, "github-copilot", StringComparison.OrdinalIgnoreCase))
            return AuthMode.Copilot;
        if (apiKey?.StartsWith("sk-ant-oat", StringComparison.Ordinal) == true)
            return AuthMode.OAuth;
        return AuthMode.ApiKey;
    }

    private static void ConfigureRequestHeaders(
        HttpRequestMessage request, string apiKey, AuthMode authMode,
        AnthropicOptions? opts, LlmModel model)
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

        if (authMode != AuthMode.Copilot)
            betaFeatures.Add("fine-grained-tool-streaming-2025-05-14");

        if (opts?.InterleavedThinking == true && !IsAdaptiveThinkingModel(model.Id))
            betaFeatures.Add("interleaved-thinking-2025-05-14");

        switch (authMode)
        {
            case AuthMode.ApiKey:
                request.Headers.Add("x-api-key", apiKey);
                break;

            case AuthMode.OAuth:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                var oauthBetas = $"claude-code-20250219,oauth-2025-04-20,{string.Join(",", betaFeatures)}";
                request.Headers.TryAddWithoutValidation("anthropic-beta", oauthBetas);
                request.Headers.TryAddWithoutValidation("user-agent", $"claude-cli/{ClaudeCodeVersion}");
                request.Headers.TryAddWithoutValidation("x-app", "cli");
                break;

            case AuthMode.Copilot:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                break;
        }

        if (betaFeatures.Count > 0 && authMode != AuthMode.OAuth)
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
            Api: "anthropic-messages",
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
        _ => throw new InvalidOperationException($"Unhandled Anthropic stop reason: {reason}")
    };

    internal static bool IsAdaptiveThinkingModel(string modelId) =>
        modelId.Contains("opus-4-6", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("opus-4.6", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("sonnet-4-6", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("sonnet-4.6", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpus46Model(string modelId) =>
        modelId.Contains("opus-4-6", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("opus-4.6", StringComparison.OrdinalIgnoreCase);
}

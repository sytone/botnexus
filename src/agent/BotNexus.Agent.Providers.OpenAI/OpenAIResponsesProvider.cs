using System.Net.Http.Headers;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.OpenAI;

/// <summary>
/// OpenAI Responses API provider.
/// Port of pi-mono's openai-responses provider + shared stream processor.
/// <para>
/// Thin shell over the shared <see cref="ResponsesStreamEngine"/> (step 6/6 of #1377): this class
/// supplies only the OpenAI transport deltas via a <see cref="ResponsesTransportProfile"/> — its
/// project-internal <see cref="OpenAIResponsesRequestBuilder"/> and the shared
/// <see cref="ResponsesStreamParser"/> (parameterized with OpenAI's service-tier resolver),
/// conditional Copilot-header decoration (applied only for github-copilot-routed models), and a plain
/// <see cref="HttpRequestException"/> error projection. The request loop, message/tool conversion, and
/// emit shapes are shared with the Copilot Responses provider.
/// </para>
/// </summary>
public sealed class OpenAIResponsesProvider(
    HttpClient httpClient,
    ILogger<OpenAIResponsesProvider> logger) : IApiProvider
{
    public string Api => "openai-responses";

    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
        => ResponsesStreamEngine.StreamAsync(BuildProfile(logger), httpClient, logger, model, context, options);

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";
        var reasoning = ModelRegistry.SupportsExtraHigh(model) ? options?.Reasoning : SimpleOptionsHelper.ClampReasoning(options?.Reasoning);
        var responsesOptions = new OpenAIResponsesOptions
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
            Metadata = options?.Metadata
        };

        if (reasoning is not null && model.Reasoning)
            responsesOptions.ReasoningEffort = MapThinkingLevel(reasoning.Value);

        return Stream(model, context, responsesOptions);
    }

    private static ResponsesTransportProfile BuildProfile(ILogger logger) => new(
        Api: "openai-responses",
        ActivityName: "provider.openai-responses.stream",
        BuildPayload: static (model, systemPrompt, messages, tools, options) =>
            OpenAIResponsesRequestBuilder.Build(
                model, systemPrompt, messages, tools, options,
                ResponsesMessageConverter.ConvertMessages, ResponsesMessageConverter.ConvertTools),
        Parse: (stream, reader, model, options, api, emitError, ct) =>
            ResponsesStreamParser.ParseAsync(
                stream, reader, model, options, api, logger, emitError,
                onParsedEvent: null,
                resolveConfiguredServiceTier: static o => o is OpenAIResponsesOptions ro ? ro.ServiceTier : null,
                normalizeTextDelta: null,
                ct),
        DecorateHeaders: static (request, model, messages, _) =>
        {
            if (string.Equals(model.Provider, "github-copilot", StringComparison.OrdinalIgnoreCase))
            {
                var hasImages = CopilotHeaders.HasVisionInput(messages);
                foreach (var (key, value) in CopilotHeaders.BuildDynamicHeaders(messages, hasImages))
                    request.Headers.TryAddWithoutValidation(key, value);
            }
        },
        ThrowForError: static (response, errorBody) =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(response, errorBody, "OpenAI"));

    private static string MapThinkingLevel(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.ExtraHigh => "xhigh",
        ThinkingLevel.Max => "xhigh",
        _ => "medium"
    };
}

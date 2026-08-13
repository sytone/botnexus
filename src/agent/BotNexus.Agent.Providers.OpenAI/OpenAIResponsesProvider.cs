using System.Net.Http.Headers;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.Core.Utilities;
using BotNexus.Gateway.Abstractions.Security;
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
/// <param name="httpClient">The shared provider HTTP client.</param>
/// <param name="logger">Stream diagnostics logger.</param>
/// <param name="secretRedactor">
/// Optional secret redactor applied to a non-2xx error body before it is interpolated into an
/// exception message that the agent loop persists as the session-visible <c>ErrorMessage</c> (#2881).
/// </param>
public sealed class OpenAIResponsesProvider(
    HttpClient httpClient,
    ILogger<OpenAIResponsesProvider> logger,
    ISecretRedactor? secretRedactor = null) : IApiProvider
{
    public string Api => "openai-responses";

    /// <summary>
    /// OpenAI Responses places the system prompt as the first message, carrying the <c>system</c> or
    /// <c>developer</c> role (see <c>OpenAIResponsesRequestBuilder</c>). No leaked-tool-call
    /// recovery: the #1709 markup leak was never observed on the OpenAI-direct API (#2432).
    /// </summary>
    public ProviderCapabilities Capabilities { get; } = new(
        RecoversLeakedToolCallMarkup: false,
        SystemPromptPlacement: SystemPromptPlacement.FirstMessage);

    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
        => ResponsesStreamEngine.StreamAsync(BuildProfile(logger, secretRedactor), httpClient, logger, model, context, options);

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        var credential = ProviderCredentialResolver.Resolve(model.Provider, options?.ApiKey, logger);
        var apiKey = credential.Value;
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

    private static ResponsesTransportProfile BuildProfile(ILogger logger, ISecretRedactor? secretRedactor) => new(
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
        ThrowForError: static (response, errorBody, redactor) =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(response, errorBody, "OpenAI", redactor),
        SecretRedactor: secretRedactor);

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

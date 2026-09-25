using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.OpenAI;

/// <summary>
/// Exposes the OpenAI-owned Responses payload builder and parser through the shared transport profile seam.
/// </summary>
public static class OpenAIResponsesTransport
{
    /// <summary>
    /// Creates a profile that reuses the OpenAI Responses wire format while allowing a provider to own transport authentication and routing.
    /// </summary>
    public static ResponsesTransportProfile CreateProfile(
        ILogger logger,
        string api = "openai-responses",
        string activityName = "provider.openai-responses.stream",
        string errorProviderName = "OpenAI",
        ISecretRedactor? secretRedactor = null,
        Func<LlmModel, Uri>? buildRequestUri = null,
        Func<HttpRequestMessage, CancellationToken, ValueTask>? authenticateRequest = null,
        bool resolveApiKey = true) => new(
        Api: api,
        ActivityName: activityName,
        BuildPayload: static (model, systemPrompt, messages, tools, options) =>
            OpenAIResponsesRequestBuilder.Build(
                model, systemPrompt, messages, tools, options,
                ResponsesMessageConverter.ConvertMessages, ResponsesMessageConverter.ConvertTools),
        Parse: (stream, reader, model, options, profileApi, emitError, ct) =>
            ResponsesStreamParser.ParseAsync(
                stream, reader, model, options, profileApi, logger, emitError,
                onParsedEvent: null,
                resolveConfiguredServiceTier: static o => o is OpenAIResponsesOptions ro ? ro.ServiceTier : null,
                ct, secretRedactor),
        DecorateHeaders: static (request, model, messages, _) =>
        {
            if (string.Equals(model.Provider, "github-copilot", StringComparison.OrdinalIgnoreCase))
            {
                var hasImages = CopilotHeaders.HasVisionInput(messages);
                foreach (var (key, value) in CopilotHeaders.BuildDynamicHeaders(messages, hasImages))
                    request.Headers.TryAddWithoutValidation(key, value);
            }
        },
        ThrowForError: (response, errorBody, redactor) =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(response, errorBody, errorProviderName, redactor),
        SecretRedactor: secretRedactor,
        BuildRequestUri: buildRequestUri,
        AuthenticateRequest: authenticateRequest,
        ResolveApiKey: resolveApiKey);
}

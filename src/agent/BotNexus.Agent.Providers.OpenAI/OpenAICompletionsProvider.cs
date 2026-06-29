using System.Net.Http.Headers;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.OpenAI;

/// <summary>
/// OpenAI Chat Completions API provider.
/// Port of pi-mono's providers/openai-completions.ts.
/// <para>
/// Thin shell over the shared <see cref="CompletionsStreamEngine"/> (step 6/6 of #1377): this class
/// supplies only the OpenAI transport deltas via a <see cref="CompletionsTransportProfile"/> —
/// conditional Copilot-header decoration (applied only for github-copilot-routed models) and a plain
/// <see cref="HttpRequestException"/> error projection. The request loop, usage parsing, stop-reason
/// mapping, tool conversion, and emit shapes are shared with the Copilot Completions provider.
/// </para>
/// </summary>
public sealed class OpenAICompletionsProvider(
    HttpClient httpClient,
    ILogger<OpenAICompletionsProvider> logger) : IApiProvider
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public string Api => "openai-completions";

    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
        => CompletionsStreamEngine.StreamAsync(BuildProfile(), _httpClient, logger, model, context, options);

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";

        var completionsOptions = new OpenAICompletionsOptions
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
            completionsOptions.ReasoningEffort = CompletionsStreamEngine.MapThinkingLevel(options.Reasoning.Value, CompatResolver.Resolve(model));

        return Stream(model, context, completionsOptions);
    }

    private static CompletionsTransportProfile BuildProfile() => new(
        Api: "openai-completions",
        ActivityName: "provider.openai-completions.stream",
        BuildPayload: static (model, systemPrompt, messages, tools, options, compat) =>
            OpenAICompletionsRequestBuilder.Build(
                model, systemPrompt, messages, tools, options, compat,
                CompletionsMessageConverter.Convert, CompletionsStreamEngine.ConvertTools),
        DecorateHeaders: static (request, model, messages, _) =>
        {
            if (string.Equals(model.Provider, "github-copilot", StringComparison.OrdinalIgnoreCase))
            {
                var hasImages = CopilotHeaders.HasVisionInput(messages);
                foreach (var (key, value) in CopilotHeaders.BuildDynamicHeaders(messages, hasImages))
                    request.Headers.TryAddWithoutValidation(key, value);
            }
        },
        ThrowForError: static (response, providerError) =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(response, providerError, "OpenAI"));
}

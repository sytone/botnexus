using System.Diagnostics;
using BotNexus.Agent.Providers.Copilot.Headers;
using BotNexus.Agent.Providers.Copilot.Telemetry;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.Core.Utilities;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Copilot.Completions;

/// <summary>
/// GitHub Copilot Chat Completions API provider. Carved out of <see cref="BotNexus.Agent.Providers.OpenAI.OpenAICompletionsProvider"/> so the Copilot transport has no cross-provider dependency on the OpenAI project. Always applies Copilot dynamic headers.
/// See <c>tests/.../CopilotCompletionsProviderParityTests</c> for the byte-identical-body proof against the legacy OpenAI-with-Copilot-auth path.
/// <para>
/// Thin shell over the shared <see cref="CompletionsStreamEngine"/> (step 6/6 of #1377): this class
/// supplies only the Copilot transport deltas via a <see cref="CompletionsTransportProfile"/> —
/// unconditional dynamic-header decoration with resolved interaction id, response-header + usage
/// telemetry hooks, and the <c>ProviderHttpErrorHelper</c> error projection. The request loop, usage
/// parsing, stop-reason mapping, tool conversion, and emit shapes are shared with the OpenAI
/// Completions provider.
/// </para>
/// </summary>
/// <param name="httpClient">The shared provider HTTP client.</param>
/// <param name="logger">Stream diagnostics logger.</param>
/// <param name="secretRedactor">
/// Optional secret redactor applied to a non-2xx error body before it is interpolated into an
/// exception message that the agent loop persists as the session-visible <c>ErrorMessage</c> (#2881).
/// </param>
public sealed class CopilotCompletionsProvider(
    HttpClient httpClient,
    ILogger<CopilotCompletionsProvider> logger,
    ISecretRedactor? secretRedactor = null) : IApiProvider
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public string Api => "github-copilot-completions";

    /// <summary>
    /// OpenAI-shaped completions over the Copilot transport: system prompt as the first message.
    /// Leaked-tool-call recovery is DECLARED because Copilot model discovery routes a given model
    /// to whichever of the three Copilot transports the account exposes, so the Claude model that
    /// produced the #1709 capture can arrive here too; #2170 is the precedent for a Copilot fix
    /// applied to one transport recurring the moment discovery selected another (#2432).
    /// </summary>
    public ProviderCapabilities Capabilities { get; } = new(
        RecoversLeakedToolCallMarkup: true,
        SystemPromptPlacement: SystemPromptPlacement.FirstMessage);

    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
        => CompletionsStreamEngine.StreamAsync(BuildProfile(secretRedactor), _httpClient, logger, model, context, options);

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        var credential = ProviderCredentialResolver.Resolve(model.Provider, options?.ApiKey, logger);
        var apiKey = credential.Value;

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
            completionsOptions.ReasoningEffort = CompletionsStreamEngine.MapThinkingLevel(options.Reasoning.Value, CompatResolver.Resolve(model));

        return Stream(model, context, completionsOptions);
    }

    private static CompletionsTransportProfile BuildProfile(ISecretRedactor? secretRedactor) => new(
        Api: "github-copilot-completions",
        ActivityName: "provider.copilot-completions.stream",
        BuildPayload: static (model, systemPrompt, messages, tools, opts, compat) =>
            CopilotCompletionsRequestBuilder.Build(
                model, systemPrompt, messages, tools, opts, compat,
                CompletionsMessageConverter.Convert, CompletionsStreamEngine.ConvertTools),
        DecorateHeaders: static (request, _, messages, opts) =>
        {
            // Copilot transport always applies the dynamic vision/intent headers —
            // this provider only handles Copilot-routed models, so the runtime check
            // present in the OpenAI parent is unnecessary.
            var hasImages = CopilotHeaders.HasVisionInput(messages);
            var headerOptions = CopilotInteractionId.WithResolvedInteractionId(
                (opts as CopilotCompletionsOptions)?.HeaderOptions);
            foreach (var (key, value) in CopilotHeaders.BuildDynamicHeaders(messages, hasImages, headerOptions))
                request.Headers.TryAddWithoutValidation(key, value);
        },
        ThrowForError: static (response, providerError, redactor) =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(response, providerError, "Copilot Completions", redactor),
        OnResponseHeaders: static response => CopilotResponseHeaders.EmitToActivity(response, Activity.Current),
        InspectChunk: static root => CopilotUsageActivity.TryParseAndEmit(root, Activity.Current),
        SecretRedactor: secretRedactor,
        // Third transport, same single normalizer (#2443). Responses and Messages already applied
        // it; Completions did not, which is the asymmetry that let #2170 reproduce #2049 verbatim
        // after model discovery switched endpoints.
        NormalizeTextDelta: static (model, delta) => CopilotTextDeltaNormalizer.Normalize(model.Id, delta));
}

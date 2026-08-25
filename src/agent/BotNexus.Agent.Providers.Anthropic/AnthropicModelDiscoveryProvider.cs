using System.Net.Http.Headers;
using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Anthropic;

/// <summary>
/// Discovers available models from the Anthropic <c>GET /v1/models</c> endpoint at startup, so the
/// Anthropic-direct model list tracks the account rather than a hardcoded table.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed, <c>BuiltInModels.RegisterAnthropicModels</c> was the only source of
/// Anthropic-direct models. A hardcoded list goes stale silently and in the worst way: a retired id
/// stays selectable in the portal, the messages request 404s, and the run surfaces as an empty
/// completion rather than an error. Discovery removes the class of failure instead of chasing it.
/// </para>
/// <para>
/// Discovery is an overlay, not a replacement. <c>ModelDiscoveryService</c> merges what this returns
/// onto the built-in registry, and a null return (no credential, network failure, timeout) leaves the
/// built-in entries untouched — so a gateway with no Anthropic key behaves exactly as it did before.
/// </para>
/// </remarks>
public sealed class AnthropicModelDiscoveryProvider : IModelDiscoveryProvider
{
    /// <inheritdoc/>
    public string ProviderKey => "anthropic";

    /// <summary>The public Anthropic API host, used when no override is supplied.</summary>
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    /// <summary>
    /// The API version header value. Pinned to the same revision <see cref="AnthropicProvider"/>
    /// sends, so discovery and inference can never disagree about the wire contract.
    /// </summary>
    private const string ApiVersion = "2023-06-01";

    /// <summary>Page size. 1000 is the endpoint's documented maximum.</summary>
    private const int PageSize = 1000;

    /// <summary>
    /// Bounds pagination. The account model list is a few dozen entries, so this is a runaway guard
    /// against a malformed <c>has_more</c> that never clears, not a real limit.
    /// </summary>
    private const int MaxPages = 10;

    /// <summary>
    /// The context window above which a model is treated as extended-context capable. Mirrors the
    /// 200K standard ceiling used by the Copilot discovery path and <c>ModelRegistry</c>.
    /// </summary>
    private const int StandardContextWindow = 200_000;

    /// <summary>Fallback context window when the payload omits <c>max_input_tokens</c>.</summary>
    private const int DefaultContextWindow = 200_000;

    /// <summary>Fallback output cap when the payload omits <c>max_tokens</c>.</summary>
    private const int DefaultMaxTokens = 8192;

    /// <summary>
    /// Discovered models carry no pricing: the endpoint does not report cost, and every entry in
    /// <c>BuiltInModels</c> already registers zero cost. Inventing a figure here would put a wrong
    /// number in front of a user, which is worse than reporting none.
    /// </summary>
    private static readonly ModelCost FreeCost = new(0, 0, 0, 0);

    private readonly HttpClient _httpClient;
    private readonly Func<CancellationToken, Task<string?>> _credentialResolver;
    private readonly ILogger<AnthropicModelDiscoveryProvider> _logger;
    private readonly string _baseUrl;

    /// <summary>
    /// Creates a new <see cref="AnthropicModelDiscoveryProvider"/>.
    /// </summary>
    /// <param name="httpClient">The shared provider HTTP client.</param>
    /// <param name="credentialResolver">
    /// Resolves the Anthropic credential. Returns null or blank when none is configured, which makes
    /// discovery a no-op rather than an error.
    /// </param>
    /// <param name="logger">Logger.</param>
    /// <param name="baseUrl">
    /// API host override. Defaults to <see cref="DefaultBaseUrl"/>. Present so a test can point the
    /// provider at a stub host without a live account.
    /// </param>
    public AnthropicModelDiscoveryProvider(
        HttpClient httpClient,
        Func<CancellationToken, Task<string?>> credentialResolver,
        ILogger<AnthropicModelDiscoveryProvider> logger,
        string? baseUrl = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialResolver = credentialResolver ?? throw new ArgumentNullException(nameof(credentialResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LlmModel>?> DiscoverModelsAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = await _credentialResolver(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogDebug("Anthropic model discovery skipped: no credential available.");
            return null;
        }

        var models = new List<LlmModel>();
        string? afterId = null;

        for (var page = 0; page < MaxPages; page++)
        {
            var response = await FetchPageAsync(apiKey, afterId, cancellationToken).ConfigureAwait(false);

            // A failed page is not a partial success: returning what we have would let a transient
            // failure silently shrink the model list, which reads to the user as models disappearing.
            if (response is null)
                return null;

            if (response.Data is not null)
            {
                foreach (var info in response.Data)
                {
                    var model = MapToLlmModel(info, _baseUrl);
                    if (model is not null)
                        models.Add(model);
                }
            }

            if (!response.HasMore || string.IsNullOrWhiteSpace(response.LastId))
                break;

            afterId = response.LastId;
        }

        if (models.Count == 0)
        {
            _logger.LogDebug("Anthropic model discovery returned no models.");
            return null;
        }

        return models;
    }

    // Fetches one page. Returns null on any non-success status or unparseable body; the caller turns
    // that into "discovery unavailable" so the built-in registry stays intact.
    private async Task<AnthropicModelsResponse?> FetchPageAsync(
        string apiKey, string? afterId, CancellationToken cancellationToken)
    {
        var url = $"{_baseUrl}/v1/models?limit={PageSize}";
        if (!string.IsNullOrWhiteSpace(afterId))
            url += $"&after_id={Uri.EscapeDataString(afterId)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The status alone is the diagnosis, and the body of a models-endpoint failure can echo
            // the credential back. Log the code, never the body.
            _logger.LogWarning(
                "Anthropic model discovery request failed with status {StatusCode}. Using built-in models.",
                (int)response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return JsonSerializer.Deserialize<AnthropicModelsResponse>(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Anthropic model discovery response could not be parsed. Using built-in models.");
            return null;
        }
    }

    /// <summary>
    /// Maps one discovered <see cref="AnthropicModelInfo"/> onto an <see cref="LlmModel"/>.
    /// </summary>
    /// <remarks>
    /// Every capability is taken from the payload when Anthropic states it, and only falls back to
    /// the shared name heuristic when it does not. That ordering matters: a stated capability is
    /// ground truth for the account, whereas the heuristic is a guess from the model id, and a guess
    /// must never override a fact.
    /// </remarks>
    /// <param name="info">The discovered model info.</param>
    /// <param name="baseUrl">The API host to stamp onto the model.</param>
    /// <returns>The mapped model, or null when the entry carries no usable id.</returns>
    public static LlmModel? MapToLlmModel(AnthropicModelInfo info, string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (string.IsNullOrWhiteSpace(info.Id))
            return null;

        var id = info.Id;
        var resolvedBaseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');
        var capabilities = info.Capabilities;

        var reasoning = capabilities?.Thinking is { } thinking
            ? thinking.Supported
            : ModelCapabilityHeuristics.IsReasoningModel(id);

        var supportsExtraHigh = ResolveExtraHighThinking(capabilities?.Effort, id);

        var advertisedWindow = info.MaxInputTokens is > 0 ? info.MaxInputTokens.Value : DefaultContextWindow;
        var maxTokens = info.MaxTokens is > 0 ? info.MaxTokens.Value : DefaultMaxTokens;

        // An advertised prompt budget beyond the standard ceiling is itself proof of a selectable
        // extended window; otherwise defer to the shared family heuristic.
        var extendedContext = advertisedWindow > StandardContextWindow
            || DynamicModelCapabilities.Infer(id, declaredExtendedContext: null).SupportsExtendedContextWindow;

        // ContextWindow is the DEFAULT tier, not the maximum the model can be pushed to. Anthropic
        // reports 1M for the long-context models, but 1M is opt-in: it is selected per request and
        // costs a beta header. BuiltInModels encodes the same distinction (Sonnet 4.5 is registered
        // at 200K with SupportsExtendedContextWindow, despite the API reporting 1M), and
        // ModelRegistry.GetSupportedContextSizes offers the extended tier separately. Registering
        // the advertised maximum here would make the beta tier look like the default and inflate
        // every budget derived from ContextWindow.
        var contextWindow = advertisedWindow > StandardContextWindow ? StandardContextWindow : advertisedWindow;

        var supportsImages = capabilities?.ImageInput?.Supported ?? true;
        IReadOnlyList<string> input = supportsImages ? ["text", "image"] : ["text"];

        return new LlmModel(
            Id: id,
            Name: string.IsNullOrWhiteSpace(info.DisplayName) ? id : info.DisplayName,
            Api: "anthropic-messages",
            Provider: "anthropic",
            BaseUrl: resolvedBaseUrl,
            Reasoning: reasoning,
            Input: input,
            Cost: FreeCost,
            ContextWindow: contextWindow,
            MaxTokens: maxTokens,
            SupportsExtraHighThinking: supportsExtraHigh,
            SupportsExtendedContextWindow: extendedContext);
    }

    /// <summary>
    /// Resolves whether a model accepts the ExtraHigh / Max thinking tiers.
    /// </summary>
    /// <remarks>
    /// Anthropic names the tiers it accepts under <c>capabilities.effort</c>, so when that node is
    /// present it is authoritative in both directions — a model that advertises effort support but
    /// neither <c>xhigh</c> nor <c>max</c> is pinned to the lower tiers even if its name suggests
    /// otherwise. Only a payload that says nothing at all falls through to the name heuristic.
    /// </remarks>
    /// <param name="effort">The advertised effort capability, when present.</param>
    /// <param name="id">The model id, used only for the fallback heuristic.</param>
    /// <returns>True when the model accepts an extra-high thinking budget.</returns>
    public static bool ResolveExtraHighThinking(AnthropicEffortCapability? effort, string id)
    {
        if (effort is null)
            return ModelCapabilityHeuristics.SupportsExtraHighThinking(id);

        return (effort.XHigh?.Supported ?? false) || (effort.Max?.Supported ?? false);
    }
}

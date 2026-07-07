using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Copilot;

/// <summary>
/// Discovers available models from the GitHub Copilot API at startup.
/// Maps <see cref="CopilotModelInfo"/> responses into <see cref="LlmModel"/>
/// entries using vendor/family heuristics to determine the correct API format.
/// </summary>
public sealed class CopilotModelDiscoveryProvider : IModelDiscoveryProvider
{
    /// <inheritdoc/>
    public string ProviderKey => "github-copilot";

    private static readonly IReadOnlyDictionary<string, string> CopilotHeaders = new Dictionary<string, string>
    {
        ["User-Agent"] = "GitHubCopilotChat/0.35.0",
        ["Editor-Version"] = "vscode/1.107.0",
        ["Editor-Plugin-Version"] = "copilot-chat/0.35.0",
        ["Copilot-Integration-Id"] = "vscode-chat"
    };

    private static readonly OpenAICompletionsCompat CopilotCompletionsCompat = new()
    {
        SupportsStore = false,
        SupportsDeveloperRole = false,
        SupportsReasoningEffort = false
    };

    private static readonly ModelCost FreeCost = new(0, 0, 0, 0);

    /// <summary>
    /// The individual GitHub Copilot host, used when no resolved endpoint is supplied (#1639).
    /// </summary>
    private const string DefaultCopilotBaseUrl = "https://api.individual.githubcopilot.com";

    private readonly CopilotDiscoveryClient _discoveryClient;
    private readonly Func<CancellationToken, Task<(string? SessionToken, string? Endpoint)>> _credentialResolver;
    private readonly ILogger<CopilotModelDiscoveryProvider> _logger;

    /// <summary>
    /// Creates a new <see cref="CopilotModelDiscoveryProvider"/>.
    /// </summary>
    /// <param name="discoveryClient">The Copilot discovery HTTP client.</param>
    /// <param name="credentialResolver">
    /// Resolves a valid Copilot session token and API endpoint.
    /// Returns (null, null) if credentials are unavailable.
    /// </param>
    /// <param name="logger">Logger.</param>
    public CopilotModelDiscoveryProvider(
        CopilotDiscoveryClient discoveryClient,
        Func<CancellationToken, Task<(string? SessionToken, string? Endpoint)>> credentialResolver,
        ILogger<CopilotModelDiscoveryProvider> logger)
    {
        _discoveryClient = discoveryClient ?? throw new ArgumentNullException(nameof(discoveryClient));
        _credentialResolver = credentialResolver ?? throw new ArgumentNullException(nameof(credentialResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LlmModel>?> DiscoverModelsAsync(CancellationToken cancellationToken = default)
    {
        var (sessionToken, endpoint) = await _credentialResolver(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(sessionToken) || string.IsNullOrWhiteSpace(endpoint))
        {
            _logger.LogDebug("Copilot model discovery skipped: no valid credentials available.");
            return null;
        }

        var response = await _discoveryClient.GetModelsAsync(endpoint, sessionToken, cancellationToken).ConfigureAwait(false);

        if (response.Data is null || response.Data.Count == 0)
        {
            _logger.LogDebug("Copilot model discovery returned no models.");
            return null;
        }

        var models = new List<LlmModel>(response.Data.Count);

        foreach (var info in response.Data)
        {
            if (string.IsNullOrWhiteSpace(info.Id))
                continue;

            var model = MapToLlmModel(info, endpoint);
            if (model is not null)
                models.Add(model);
        }

        return models;
    }

    /// <summary>
    /// Maps a <see cref="CopilotModelInfo"/> to an <see cref="LlmModel"/>.
    /// Uses vendor + family heuristics to determine API format, reasoning support,
    /// and input modalities.
    /// </summary>
    public static LlmModel? MapToLlmModel(CopilotModelInfo info)
        => MapToLlmModel(info, DefaultCopilotBaseUrl);

    /// <summary>
    /// Maps a <see cref="CopilotModelInfo"/> to an <see cref="LlmModel"/>, stamping the supplied
    /// resolved host onto <see cref="LlmModel.BaseUrl"/>. #1639: the discovered model is born with
    /// the CORRECT host (enterprise vs individual GitHub Copilot) so no consumer patches BaseUrl.
    /// A null/whitespace <paramref name="baseUrl"/> falls back to the individual host.
    /// </summary>
    /// <param name="info">The Copilot model info.</param>
    /// <param name="baseUrl">The resolved API host to stamp onto the model.</param>
    public static LlmModel? MapToLlmModel(CopilotModelInfo info, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(info.Id))
            return null;

        var resolvedBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultCopilotBaseUrl : baseUrl;

        var id = info.Id;
        var name = info.Name ?? info.Id;
        var family = info.Capabilities?.Family ?? string.Empty;
        var vendor = info.Vendor ?? string.Empty;

        var api = ResolveApiFormat(id, family, vendor);
        var reasoning = IsReasoningModel(id, family);
        var supportsExtraHigh = SupportsExtraHighThinking(id, family);
        var input = ResolveInputModalities(info);
        var (contextWindow, maxTokens) = ResolveLimits(info);
        var compat = api == "github-copilot-completions" ? CopilotCompletionsCompat : null;

        return new LlmModel(
            Id: id,
            Name: name,
            Api: api,
            Provider: "github-copilot",
            BaseUrl: resolvedBaseUrl,
            Reasoning: reasoning,
            Input: input,
            Cost: FreeCost,
            ContextWindow: contextWindow,
            MaxTokens: maxTokens,
            SupportsExtraHighThinking: supportsExtraHigh,
            Headers: CopilotHeaders,
            Compat: compat);
    }

    /// <summary>
    /// Resolves the API format based on model family/vendor/id heuristics.
    /// </summary>
    public static string ResolveApiFormat(string id, string family, string vendor)
    {
        // Claude models use the messages API
        if (family.StartsWith("claude", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
            return "github-copilot-messages";

        // GPT-5+ and o-series use the responses API
        if (id.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("o4", StringComparison.OrdinalIgnoreCase))
            return "github-copilot-responses";

        // Everything else (GPT-4, Gemini, Grok, etc.) uses completions
        return "github-copilot-completions";
    }

    /// <summary>
    /// Determines if a model supports reasoning/thinking based on family and id.
    /// </summary>
    public static bool IsReasoningModel(string id, string family)
    {
        // Claude 4+ and Sonnet 4.5+ support thinking
        if (id.StartsWith("claude-opus-4", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("claude-sonnet-4", StringComparison.OrdinalIgnoreCase))
            return true;

        // GPT-5+ supports reasoning
        if (id.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase))
            return true;

        // o-series models
        if (id.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("o4", StringComparison.OrdinalIgnoreCase))
            return true;

        // Gemini 3+
        if (id.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase))
            return true;

        // Grok code
        if (id.StartsWith("grok-code", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Determines if a model supports extra-high thinking budget.
    /// </summary>
    public static bool SupportsExtraHighThinking(string id, string family)
    {
        // Claude Opus 4.6+ supports extra high
        if (id.StartsWith("claude-opus-4.", StringComparison.OrdinalIgnoreCase))
        {
            var versionPart = id.AsSpan()["claude-opus-4.".Length..];
            if (versionPart.Length > 0 && char.IsDigit(versionPart[0]) && versionPart[0] >= '6')
                return true;
        }

        // GPT 5.2+
        if (id.StartsWith("gpt-5.", StringComparison.OrdinalIgnoreCase))
        {
            var versionPart = id.AsSpan()["gpt-5.".Length..];
            if (versionPart.Length > 0 && char.IsDigit(versionPart[0]) && versionPart[0] >= '2')
                return true;
        }

        return false;
    }

    private static IReadOnlyList<string> ResolveInputModalities(CopilotModelInfo info)
    {
        var supportsVision = info.Capabilities?.Supports?.Vision ?? false;
        return supportsVision ? ["text", "image"] : ["text"];
    }

    private static (int ContextWindow, int MaxTokens) ResolveLimits(CopilotModelInfo info)
    {
        var limits = info.Capabilities?.Limits;
        int contextWindow = 128000; // default
        int maxTokens = 32000; // default

        if (limits is null)
            return (contextWindow, maxTokens);

        if (limits.TryGetValue("max_prompt_tokens", out var promptEl) && promptEl.TryGetInt32(out var promptTokens))
            contextWindow = promptTokens;

        if (limits.TryGetValue("max_output_tokens", out var outputEl) && outputEl.TryGetInt32(out var outputTokens))
            maxTokens = outputTokens;

        return (contextWindow, maxTokens);
    }
}

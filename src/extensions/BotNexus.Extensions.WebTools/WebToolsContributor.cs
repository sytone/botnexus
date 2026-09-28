using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.WebTools;

/// <summary>
/// Contributes web fetch and search tools from per-agent extension configuration.
/// </summary>
public sealed class WebToolsContributor : IAgentToolContributor
{
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ISecretRedactor? _secretRedactor;
    private readonly Func<WebFetchConfig, PublicNetworkHttpTransport> _transportFactory;

    /// <summary>
    /// Creates the contributor.
    /// </summary>
    /// <param name="loggerFactory">Optional logger factory for the contributed tools.</param>
    /// <param name="secretRedactor">
    /// Optional secret redactor (#3360), resolved from DI when the host has registered one. Both
    /// web tools return untrusted, server-influenced error text to the model, and that text is
    /// persisted to the transcript; this is where the redaction seam #2881 introduced for the
    /// provider path is threaded into the extension. Optional rather than required on purpose --
    /// the extension load context activates contributors from the host container and an
    /// unsatisfiable constructor parameter would get the whole contributor pruned at startup
    /// (see <c>PruneUnconstructableExtensionServices</c>), silently removing both web tools. A
    /// null redactor is a pass-through no-op inside the tools, so an unwired host keeps its
    /// diagnostics.
    /// </param>
    public WebToolsContributor(
        ILoggerFactory? loggerFactory = null,
        ISecretRedactor? secretRedactor = null)
    {
        _loggerFactory = loggerFactory;
        _secretRedactor = secretRedactor;
        _transportFactory = config => new PublicNetworkHttpTransport(config);
    }

    // Preserve the public DI constructor; tests replace only DNS/socket dependencies inside
    // the real transport, never the contributor's owned-client construction path.
    internal WebToolsContributor(Func<WebFetchConfig, PublicNetworkHttpTransport> transportFactory)
        : this()
    {
        _transportFactory = transportFactory;
    }

    /// <inheritdoc />
    public Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var defaults = ExtensionConfigBinder.BindAgentDefaults<WebToolsOverrides>(context.Descriptor, "botnexus-web");
        var named = ExtensionConfigBinder.BindNamedAgent<WebToolsOverrides>(context.Descriptor, "botnexus-web");
        if (defaults is null && named is null)
            return Task.FromResult(new AgentToolContribution([]));

        var tools = new List<IAgentTool>();
        var fetchConfig = ResolveFetch(named?.Fetch, defaults?.Fetch);
        tools.Add(new WebFetchTool(fetchConfig, _transportFactory(fetchConfig), _secretRedactor));

        if (ResolveSearch(named?.Search, defaults?.Search) is { } searchConfig)
        {
            var useCopilotProvider = string.Equals(searchConfig.Provider, "copilot", StringComparison.OrdinalIgnoreCase);
            var hasApiKey = !string.IsNullOrWhiteSpace(searchConfig.ApiKey);

            if (useCopilotProvider || hasApiKey)
            {
                var copilotApiEndpoint = useCopilotProvider
                    ? context.CopilotMcpEndpoint
                    : null;

                tools.Add(new WebSearchTool(
                    searchConfig,
                    copilotApiKeyResolver: useCopilotProvider
                        ? ct => context.GetProviderApiKeyAsync(context.Descriptor.ApiProvider, ct)
                        : null,
                    copilotApiEndpoint: copilotApiEndpoint,
                    logger: _loggerFactory?.CreateLogger<WebSearchTool>(),
                    secretRedactor: _secretRedactor));
            }
        }

        return Task.FromResult(new AgentToolContribution(tools));
    }

    private static WebSearchConfig? ResolveSearch(WebSearchOverrides? named, WebSearchOverrides? defaults)
    {
        if (named is null && defaults is null)
            return null;
        return new WebSearchConfig
        {
            Provider = named?.Provider ?? defaults?.Provider ?? "brave",
            ApiKey = named?.ApiKey ?? defaults?.ApiKey,
            MaxResults = named?.MaxResults ?? defaults?.MaxResults ?? 5
        };
    }

    private sealed class WebToolsOverrides
    {
        public WebSearchOverrides? Search { get; set; }
        public WebFetchOverrides? Fetch { get; set; }
    }

    private sealed class WebSearchOverrides
    {
        public string? Provider { get; set; }
        public string? ApiKey { get; set; }
        public int? MaxResults { get; set; }
    }

    private sealed class WebFetchOverrides
    {
        public int? MaxLengthChars { get; set; }
        public int? TimeoutSeconds { get; set; }
        public string? UserAgent { get; set; }
        public bool? AllowPrivateNetworks { get; set; }
        public IReadOnlyList<string>? AdditionalBlockedHosts { get; set; }
        public long? MaxResponseBytes { get; set; }
    }

    private static WebFetchConfig ResolveFetch(WebFetchOverrides? named, WebFetchOverrides? defaults)
        => new()
        {
            MaxLengthChars = named?.MaxLengthChars ?? defaults?.MaxLengthChars ?? 20_000,
            TimeoutSeconds = named?.TimeoutSeconds ?? defaults?.TimeoutSeconds ?? 30,
            UserAgent = named?.UserAgent ?? defaults?.UserAgent ?? "BotNexus/1.0 (compatible; bot)",
            AllowPrivateNetworks = named?.AllowPrivateNetworks ?? defaults?.AllowPrivateNetworks ?? false,
            AdditionalBlockedHosts = named?.AdditionalBlockedHosts ?? defaults?.AdditionalBlockedHosts ?? [],
            MaxResponseBytes = named?.MaxResponseBytes ?? defaults?.MaxResponseBytes ?? 16L * 1024 * 1024
        };
}

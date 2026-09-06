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

        var config = ResolveExtensionConfig<WebToolsConfig>(context.Descriptor, "botnexus-web");
        if (config is null)
            return Task.FromResult(new AgentToolContribution([]));

        var tools = new List<IAgentTool>();
        var fetchConfig = config.Fetch ?? new WebFetchConfig();
        tools.Add(new WebFetchTool(fetchConfig, _transportFactory(fetchConfig), _secretRedactor));

        if (config.Search is { } searchConfig)
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

    private static T? ResolveExtensionConfig<T>(AgentDescriptor descriptor, string extensionId) where T : class
        => ExtensionConfigBinder.Bind<T>(descriptor, extensionId);
}

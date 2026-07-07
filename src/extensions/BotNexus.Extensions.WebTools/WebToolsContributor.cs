using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.WebTools;

/// <summary>
/// Contributes web fetch and search tools from per-agent extension configuration.
/// </summary>
public sealed class WebToolsContributor : IAgentToolContributor
{
    private readonly ILoggerFactory? _loggerFactory;

    public WebToolsContributor(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
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
        tools.Add(new WebFetchTool(fetchConfig));

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
                    logger: _loggerFactory?.CreateLogger<WebSearchTool>()));
            }
        }

        return Task.FromResult(new AgentToolContribution(tools));
    }

    private static T? ResolveExtensionConfig<T>(AgentDescriptor descriptor, string extensionId) where T : class
    {
        if (descriptor.ExtensionConfig.TryGetValue(extensionId, out var element))
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<T>(element.GetRawText());
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}

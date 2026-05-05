using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.WebTools;

/// <summary>
/// Contributes web fetch and search tools from per-agent extension configuration.
/// </summary>
public sealed class WebToolsContributor : IAgentToolContributor
{
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
                    ? ResolveCopilotMcpEndpoint(context.GetProviderEndpoint(context.Descriptor.ApiProvider))
                    : null;

                tools.Add(new WebSearchTool(
                    searchConfig,
                    copilotApiKeyResolver: useCopilotProvider
                        ? ct => context.GetProviderApiKeyAsync(context.Descriptor.ApiProvider, ct)
                        : null,
                    copilotApiEndpoint: copilotApiEndpoint));
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

    private static string ResolveCopilotMcpEndpoint(string? baseEndpoint)
    {
        const string fallbackEndpoint = "https://api.githubcopilot.com/mcp";
        if (string.IsNullOrWhiteSpace(baseEndpoint))
            return fallbackEndpoint;

        if (Uri.TryCreate(baseEndpoint, UriKind.Absolute, out var absoluteUri))
        {
            var path = absoluteUri.AbsolutePath.TrimEnd('/');
            if (path.EndsWith("/mcp", StringComparison.OrdinalIgnoreCase))
                return absoluteUri.ToString().TrimEnd('/');

            var builder = new UriBuilder(absoluteUri)
            {
                Path = string.IsNullOrEmpty(path) || path == "/" ? "/mcp" : $"{path}/mcp"
            };

            return builder.Uri.ToString().TrimEnd('/');
        }

        var trimmed = baseEndpoint.TrimEnd('/');
        return trimmed.EndsWith("/mcp", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}/mcp";
    }
}

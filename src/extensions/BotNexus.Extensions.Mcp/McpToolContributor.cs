using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Mcp;

/// <summary>
/// Contributes MCP-bridged tools from the non-blocking server warmup cache.
/// HTTP/SSE servers with an <c>auth</c> provider reference are started fresh per session
/// with a resolved Bearer token; all other servers use the shared warmup cache.
/// </summary>
public sealed class McpToolContributor(ILoggerFactory loggerFactory) : IAgentToolContributor
{
    /// <inheritdoc />
    public async Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var config = ResolveMcpExtensionConfig(context.Descriptor);
        if (config is not { Servers.Count: > 0 })
            return new AgentToolContribution([]);

        // Split into servers that need auth injection and those that do not.
        var warmupServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        var authServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);

        foreach (var (id, srv) in config.Servers)
        {
            if (!string.IsNullOrWhiteSpace(srv.Auth) && !string.IsNullOrWhiteSpace(srv.Url))
                authServers[id] = srv;
            else
                warmupServers[id] = srv;
        }

        var tools = new List<IAgentTool>();
        var resourcesToDispose = new List<object>();

        // Warmup-cache path for non-auth servers.
        if (warmupServers.Count > 0)
        {
            var warmupConfig = new McpExtensionConfig
            {
                Servers = warmupServers,
                ToolPrefix = config.ToolPrefix,
            };

            var entry = McpServerWarmupCache.EnsureStarted(
                context.Descriptor.AgentId.Value,
                warmupConfig,
                loggerFactory.CreateLogger<McpServerManager>());

            if (entry.TryGetReadyTools(out var warmupTools))
                tools.AddRange(warmupTools);
        }

        // Per-session path for auth servers — resolve token and inject header.
        foreach (var (serverId, serverConfig) in authServers)
        {
            var token = await context.GetProviderApiKeyAsync(serverConfig.Auth!, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(token))
            {
                loggerFactory.CreateLogger<McpToolContributor>().LogWarning(
                    "MCP server '{ServerId}' has auth={Auth} configured but no token was resolved. Skipping server.",
                    serverId, serverConfig.Auth);
                continue;
            }

            // Build a hydrated config with the injected Authorization header.
            // Explicit Headers.Authorization wins over auth-resolved token.
            var mergedHeaders = new Dictionary<string, string>(
                serverConfig.Headers ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);

            if (!mergedHeaders.ContainsKey("Authorization"))
                mergedHeaders["Authorization"] = $"Bearer {token}";

            var hydratedConfig = new McpServerConfig
            {
                Url = serverConfig.Url,
                Headers = mergedHeaders,
                InitTimeoutMs = serverConfig.InitTimeoutMs,
                CallTimeoutMs = serverConfig.CallTimeoutMs,
                // Auth is cleared so CreateTransport does not see a stale value
            };

            var manager = new McpServerManager(loggerFactory.CreateLogger<McpServerManager>());
            resourcesToDispose.Add(manager);

            var serverTools = await manager.StartSingleServerAsync(
                serverId, hydratedConfig, config.ToolPrefix, cancellationToken)
                .ConfigureAwait(false);

            tools.AddRange(serverTools);
        }

        return new AgentToolContribution(tools, resourcesToDispose.Count > 0 ? resourcesToDispose : null);
    }

    internal static McpExtensionConfig? ResolveMcpExtensionConfig(AgentDescriptor descriptor)
        => ResolveExtensionConfig<McpExtensionConfig>(descriptor, "botnexus-mcp");

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

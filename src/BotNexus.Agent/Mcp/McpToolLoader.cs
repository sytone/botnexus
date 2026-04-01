using System.Text.Json.Nodes;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using BotNexus.Agent.Tools;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Mcp;

/// <summary>
/// Connects to all configured MCP servers, discovers their tools, and registers each one
/// in the <see cref="ToolRegistry"/> as an <see cref="McpTool"/>.
/// </summary>
public sealed class McpToolLoader : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, McpServerConfig> _serverConfigs;
    private readonly ILogger<McpToolLoader> _logger;
    private readonly List<IMcpClient> _clients = [];

    public McpToolLoader(
        IReadOnlyDictionary<string, McpServerConfig> serverConfigs,
        ILogger<McpToolLoader> logger)
    {
        _serverConfigs = serverConfigs;
        _logger = logger;
    }

    /// <summary>
    /// Initialises all MCP server connections and loads their tools into the registry.
    /// Servers that fail to connect are logged and skipped.
    /// </summary>
    public async Task LoadAsync(ToolRegistry registry, CancellationToken cancellationToken = default)
    {
        foreach (var (logicalName, config) in _serverConfigs)
        {
            // Use logical name as server identifier when no explicit Name is set
            var serverName = string.IsNullOrWhiteSpace(config.Name) ? logicalName : config.Name;
            config.Name = serverName;

            var client = new McpClient(config, _logger);
            try
            {
                await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
                _clients.Add(client);
                RegisterClientTools(client, config, registry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MCP server '{Name}' failed to initialize, skipping", serverName);
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void RegisterClientTools(IMcpClient client, McpServerConfig config, ToolRegistry registry)
    {
        var enableAll = config.EnabledTools is { Count: 1 } && config.EnabledTools[0] == "*";
        var enabledSet = new HashSet<string>(config.EnabledTools, StringComparer.OrdinalIgnoreCase);

        foreach (var (remoteName, schema) in client.RemoteTools)
        {
            // Apply enabledTools filter
            if (!enableAll && !enabledSet.Contains(remoteName) && !enabledSet.Contains($"mcp_{config.Name}_{remoteName}"))
            {
                _logger.LogDebug("MCP tool '{Server}/{Tool}' excluded by enabledTools filter", config.Name, remoteName);
                continue;
            }

            var toolDef = BuildToolDefinition(config.Name, remoteName, schema);
            var mcpTool = new McpTool(client, remoteName, toolDef, _logger);
            registry.Register(mcpTool);
            _logger.LogDebug("Registered MCP tool '{ToolName}' from server '{Server}'", toolDef.Name, config.Name);
        }
    }

    /// <summary>Builds a <see cref="ToolDefinition"/> from an MCP JSON schema object.</summary>
    private static ToolDefinition BuildToolDefinition(string serverName, string remoteName, JsonObject schema)
    {
        // Prefix the tool name with the server name to avoid collisions across servers
        var toolName = $"mcp_{serverName}_{remoteName}";
        var description = schema["description"]?.GetValue<string>()
            ?? $"MCP tool '{remoteName}' from server '{serverName}'";

        var parameters = new Dictionary<string, ToolParameterSchema>();
        var inputSchema = schema["inputSchema"] as JsonObject;
        var properties = inputSchema?["properties"] as JsonObject;
        var requiredArray = inputSchema?["required"]?.AsArray();
        var requiredNames = new HashSet<string>(
            requiredArray?.Select(n => n?.GetValue<string>() ?? string.Empty) ?? [],
            StringComparer.OrdinalIgnoreCase);

        if (properties is not null)
        {
            foreach (var (propName, propNode) in properties)
            {
                if (propNode is not JsonObject prop) continue;
                var type = prop["type"]?.GetValue<string>() ?? "string";
                var desc = prop["description"]?.GetValue<string>() ?? propName;
                var enumValues = prop["enum"]?.AsArray()
                    ?.Select(e => e?.GetValue<string>() ?? string.Empty)
                    .ToList();
                parameters[propName] = new ToolParameterSchema(
                    type,
                    desc,
                    Required: requiredNames.Contains(propName),
                    EnumValues: enumValues);
            }
        }

        return new ToolDefinition(toolName, description, parameters);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync().ConfigureAwait(false);
        _clients.Clear();
    }
}

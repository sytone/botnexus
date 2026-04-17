using System.Collections.Concurrent;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Security;

/// <summary>
/// Default tool policy provider with built-in dangerous tool classifications
/// derived from the OpenClaw security baseline. MCP-bridged tools (named
/// <c>{serverId}_{toolName}</c>) default to <see cref="ToolRiskLevel.Moderate"/>.
/// </summary>
public sealed class DefaultToolPolicyProvider : IToolPolicyProvider
{
    private static readonly HashSet<string> DangerousTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "exec", "write", "edit", "process", "bash"
    };

    private static readonly List<string> HttpDeniedTools =
    [
        "sessions_spawn",
        "sessions_send",
        "cron",
        "gateway",
        "whatsapp_login"
    ];

    private static readonly HashSet<string> HttpDeniedLookup = new(HttpDeniedTools, StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, byte> _mcpServerIds = new(StringComparer.OrdinalIgnoreCase);

    private readonly IOptionsMonitor<PlatformConfig> _config;
    private readonly ILogger<DefaultToolPolicyProvider> _logger;

    public DefaultToolPolicyProvider(
        IOptionsMonitor<PlatformConfig> config,
        ILogger<DefaultToolPolicyProvider> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Registers an MCP server ID so that tools prefixed with this ID
    /// are classified as <see cref="ToolRiskLevel.Moderate"/> by default.
    /// </summary>
    public void RegisterMcpServerId(string serverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        _mcpServerIds.TryAdd(serverId, 0);
        _logger.LogDebug("Registered MCP server ID '{ServerId}' for tool policy", serverId);
    }

    /// <summary>
    /// Returns the set of registered MCP server IDs.
    /// </summary>
    internal IReadOnlyCollection<string> McpServerIds => _mcpServerIds.Keys.ToArray();

    /// <inheritdoc />
    public ToolRiskLevel GetRiskLevel(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        if (HttpDeniedLookup.Contains(toolName))
            return ToolRiskLevel.Dangerous;

        if (DangerousTools.Contains(toolName))
            return ToolRiskLevel.Dangerous;

        // MCP tools are named {serverId}_{toolName} — default to Moderate
        if (IsMcpTool(toolName))
            return ToolRiskLevel.Moderate;

        return ToolRiskLevel.Safe;
    }

    /// <inheritdoc />
    public bool RequiresApproval(string toolName, string? agentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        // Check per-agent overrides from config
        if (agentId is not null)
        {
            var agentPolicy = GetAgentToolPolicy(agentId);
            if (agentPolicy is not null)
            {
                // Explicitly trusted tools skip approval
                if (agentPolicy.NeverApprove?.Contains(toolName, StringComparer.OrdinalIgnoreCase) == true)
                {
                    _logger.LogDebug(
                        "Tool {ToolName} skips approval for agent {AgentId} (per-agent NeverApprove override)",
                        toolName, agentId);
                    return false;
                }

                // Explicitly requiring approval overrides default
                if (agentPolicy.AlwaysApprove?.Contains(toolName, StringComparer.OrdinalIgnoreCase) == true)
                    return true;
            }
        }

        return DangerousTools.Contains(toolName);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetDeniedForHttp() => HttpDeniedTools;

    /// <summary>
    /// Checks whether a tool is completely blocked for a specific agent.
    /// Supports MCP wildcard deny via <c>serverId_*</c> patterns in the denied list.
    /// </summary>
    internal bool IsDenied(string toolName, string? agentId)
    {
        if (agentId is null)
            return false;

        var agentPolicy = GetAgentToolPolicy(agentId);
        if (agentPolicy?.Denied is null)
            return false;

        // Exact match
        if (agentPolicy.Denied.Contains(toolName, StringComparer.OrdinalIgnoreCase))
            return true;

        // Wildcard server-level deny: "serverId_*" blocks all tools from that server
        var underscoreIdx = toolName.IndexOf('_', StringComparison.Ordinal);
        if (underscoreIdx > 0)
        {
            var serverPrefix = toolName[..underscoreIdx];
            var wildcardPattern = $"{serverPrefix}_*";
            if (agentPolicy.Denied.Contains(wildcardPattern, StringComparer.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns <c>true</c> if the tool name matches the MCP naming convention
    /// <c>{serverId}_{toolName}</c> where the prefix is a registered MCP server ID.
    /// </summary>
    internal bool IsMcpTool(string toolName)
    {
        var underscoreIdx = toolName.IndexOf('_', StringComparison.Ordinal);
        if (underscoreIdx <= 0)
            return false;

        var prefix = toolName[..underscoreIdx];
        return _mcpServerIds.ContainsKey(prefix);
    }

    private ToolPolicyConfig? GetAgentToolPolicy(string agentId)
    {
        var config = _config.CurrentValue;
        if (config.Agents is null || !config.Agents.TryGetValue(agentId, out var agentConfig))
            return null;

        return agentConfig.ToolPolicy;
    }
}

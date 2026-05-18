using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
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

    // Dynamic deny-lists for ephemeral sub-agents that have no PlatformConfig entry.
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _dynamicDenyLists = new(StringComparer.OrdinalIgnoreCase);

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
    /// Sets the effective deny-list for an ephemeral sub-agent session.
    /// Called when a sub-agent is spawned to propagate the parent's restrictions.
    /// </summary>
    public void SetDynamicDenyList(AgentId agentId, IReadOnlyList<string> denyList)
    {
        _dynamicDenyLists[agentId.Value] = denyList;
        _logger.LogDebug("Set dynamic deny-list ({Count} entries) for sub-agent '{AgentId}'", denyList.Count, agentId);
        OnDynamicDenyListSet?.Invoke(agentId, denyList);
    }

    /// <summary>
    /// Removes the dynamic deny-list for a sub-agent session when it is torn down.
    /// </summary>
    public void RemoveDynamicDenyList(AgentId agentId)
    {
        if (_dynamicDenyLists.TryRemove(agentId.Value, out _))
            _logger.LogDebug("Removed dynamic deny-list for sub-agent '{AgentId}'", agentId);
    }

    /// <summary>
    /// Test hook: invoked after each successful <see cref="SetDynamicDenyList"/> call.
    /// Allows tests to verify the child deny-list without requiring a full DI setup.
    /// </summary>
    internal Action<AgentId, IReadOnlyList<string>>? OnDynamicDenyListSet { get; set; }

    /// <summary>
    /// Returns the union of the static config deny-list and any dynamic deny-list for the given agent.
    /// </summary>
    public IReadOnlyList<string> GetEffectiveDenyList(string agentId)
    {
        var agentPolicy = GetAgentToolPolicy(agentId);
        var configDenied = agentPolicy?.Denied ?? [];
        if (!_dynamicDenyLists.TryGetValue(agentId, out var dynamic))
            return configDenied;

        return configDenied.Union(dynamic, StringComparer.OrdinalIgnoreCase).ToList();
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
    /// Checks both static config deny-lists and runtime dynamic deny-lists (for sub-agents).
    /// Supports MCP wildcard deny via <c>serverId_*</c> patterns in the denied list.
    /// </summary>
    internal bool IsDenied(string toolName, string? agentId)
    {
        if (agentId is null)
            return false;

        return IsInDenyList(toolName, GetEffectiveDenyList(agentId));
    }

    private static bool IsInDenyList(string toolName, IReadOnlyList<string> denied)
    {
        // Exact match
        if (denied.Contains(toolName, StringComparer.OrdinalIgnoreCase))
            return true;

        // Wildcard server-level deny: "serverId_*" blocks all tools from that server
        var underscoreIdx = toolName.IndexOf('_', StringComparison.Ordinal);
        if (underscoreIdx > 0)
        {
            var serverPrefix = toolName[..underscoreIdx];
            var wildcardPattern = $"{serverPrefix}_*";
            if (denied.Contains(wildcardPattern, StringComparer.OrdinalIgnoreCase))
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

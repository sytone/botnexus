using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Security;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Hooks;

/// <summary>
/// Hook handler that enforces tool policy before tool execution.
/// Denies blocked tools and logs warnings for tools requiring approval.
/// </summary>
public sealed class ToolPolicyHookHandler : IHookHandler<BeforeToolCallEvent, BeforeToolCallResult>
{
    private readonly DefaultToolPolicyProvider _policyProvider;
    private readonly ILogger<ToolPolicyHookHandler> _logger;

    /// <summary>Runs early to enforce policy before other handlers.</summary>
    public int Priority => -100;

    public ToolPolicyHookHandler(
        DefaultToolPolicyProvider policyProvider,
        ILogger<ToolPolicyHookHandler> logger)
    {
        _policyProvider = policyProvider;
        _logger = logger;
    }

    public Task<BeforeToolCallResult?> HandleAsync(BeforeToolCallEvent hookEvent, CancellationToken ct = default)
    {
        // Check if tool is completely denied for this agent
        if (_policyProvider.IsDenied(hookEvent.ToolName, hookEvent.AgentId.Value))
        {
            _logger.LogWarning(
                "Tool {ToolName} is denied for agent {AgentId} by tool policy",
                hookEvent.ToolName, hookEvent.AgentId);

            return Task.FromResult<BeforeToolCallResult?>(new BeforeToolCallResult
            {
                Denied = true,
                DenyReason = $"Tool '{hookEvent.ToolName}' is blocked by agent tool policy."
            });
        }

        // Check if tool requires approval — log a warning for now (full approval UI comes later)
        if (_policyProvider.RequiresApproval(hookEvent.ToolName, hookEvent.AgentId.Value))
        {
            _logger.LogWarning(
                "Tool {ToolName} requires approval for agent {AgentId} (risk level: {RiskLevel}). " +
                "Approval workflow not yet implemented — allowing execution with warning.",
                hookEvent.ToolName,
                hookEvent.AgentId,
                _policyProvider.GetRiskLevel(hookEvent.ToolName));
        }

        return Task.FromResult<BeforeToolCallResult?>(null);
    }
}

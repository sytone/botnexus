using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Security;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Hooks;

/// <summary>
/// Hook handler that enforces tool policy before tool execution. Denies blocked tools and
/// applies the configured approval fallback posture to approval-required tools.
/// </summary>
/// <remarks>
/// <para>
/// Issue #2391. Previously the approval branch logged a warning and returned <c>null</c>, so every
/// approval-required tool executed unapproved -- the highest-volume warning in the gateway log.
/// The branch now consults <see cref="IToolPolicyProvider.GetApprovalFallback"/>.
/// </para>
/// <para>
/// <b>Why the default stays <c>allow</c>.</b> A fallback that could genuinely ask the user must be
/// able to reach a conversation, and <see cref="BeforeToolCallEvent"/> carries no conversation
/// identity -- only <c>AgentId</c>, <c>ToolName</c>, <c>ToolCallId</c> and arguments. There is
/// therefore no route from this seam to the channel-agnostic <c>ask_user</c> resolver
/// (<c>IAskUserPromptResolver</c>, #2322/#2334) without a contract change. Denying by default would
/// break every unattended agent, cron job, and sub-agent that runs <c>exec</c>/<c>write</c>/
/// <c>edit</c>. So the default posture is unchanged and the deny posture is opt-in per agent, with
/// the difference that the allow path is now an explicit, audited policy decision rather than a
/// silent fall-through.
/// </para>
/// </remarks>
public sealed class ToolPolicyHookHandler : IHookHandler<BeforeToolCallEvent, BeforeToolCallResult>
{
    /// <summary>
    /// Prefix on the deny reason returned when the approval fallback refuses execution. Mirrors
    /// OpenClaw's <c>ask-fallback-deny</c> marker so the cause is distinguishable from an
    /// ordinary deny-list block.
    /// </summary>
    internal const string AskFallbackDenyReasonPrefix = "ask-fallback-deny";

    private readonly DefaultToolPolicyProvider _policyProvider;
    private readonly ISecurityEventSink? _securityEvents;
    private readonly ILogger<ToolPolicyHookHandler> _logger;

    /// <summary>Runs early to enforce policy before other handlers.</summary>
    public int Priority => -100;

    /// <summary>
    /// Creates the handler. The security-event sink is optional; when supplied, every approval
    /// fallback decision emits one <see cref="SecurityEvent"/>. Emission is best-effort and never
    /// changes the policy outcome.
    /// </summary>
    /// <param name="policyProvider">Tool policy source.</param>
    /// <param name="logger">Logger for policy decisions.</param>
    /// <param name="securityEvents">Trusted security-event sink, or null to disable emission.</param>
    public ToolPolicyHookHandler(
        DefaultToolPolicyProvider policyProvider,
        ILogger<ToolPolicyHookHandler> logger,
        ISecurityEventSink? securityEvents = null)
    {
        _policyProvider = policyProvider;
        _logger = logger;
        _securityEvents = securityEvents;
    }

    /// <inheritdoc />
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

        if (!_policyProvider.RequiresApproval(hookEvent.ToolName, hookEvent.AgentId.Value))
            return Task.FromResult<BeforeToolCallResult?>(null);

        // Approval is required and there is no workflow at this seam that can obtain it.
        // Apply the configured fallback posture instead of falling through silently.
        var fallback = _policyProvider.GetApprovalFallback(hookEvent.ToolName, hookEvent.AgentId.Value);
        var riskLevel = _policyProvider.GetRiskLevel(hookEvent.ToolName);

        if (fallback == ToolApprovalFallback.Deny)
        {
            _logger.LogWarning(
                "Tool {ToolName} requires approval for agent {AgentId} (risk level: {RiskLevel}) and "
                + "no approval workflow is available; askFallback=deny so execution is refused.",
                hookEvent.ToolName, hookEvent.AgentId, riskLevel);

            EmitDecision(
                "tool.execution.blocked",
                SecurityPolicyDecision.Deny,
                hookEvent.AgentId.Value,
                hookEvent.ToolName);

            return Task.FromResult<BeforeToolCallResult?>(new BeforeToolCallResult
            {
                Denied = true,
                DenyReason =
                    $"{AskFallbackDenyReasonPrefix}: tool '{hookEvent.ToolName}' requires approval and no approval "
                    + "workflow is available for this agent."
            });
        }

        // askFallback=allow: unchanged behaviour, but now an explicit, audited policy decision.
        _logger.LogDebug(
            "Tool {ToolName} requires approval for agent {AgentId} (risk level: {RiskLevel}); "
            + "askFallback=allow so execution proceeds.",
            hookEvent.ToolName, hookEvent.AgentId, riskLevel);

        EmitDecision(
            "tool.execution.approval.fallback.allowed",
            SecurityPolicyDecision.Allow,
            hookEvent.AgentId.Value,
            hookEvent.ToolName);

        return Task.FromResult<BeforeToolCallResult?>(null);
    }

    /// <summary>
    /// Emits one approval-boundary security event. Best-effort: a null sink is a no-op and any
    /// sink fault is swallowed so observability can never change a policy outcome.
    /// </summary>
    private void EmitDecision(string action, SecurityPolicyDecision decision, string agentId, string toolName)
    {
        if (_securityEvents is null)
            return;

        try
        {
            var evt = SecurityEvent.ApprovalDecision(
                action,
                decision,
                actor: new SecurityEventActor(SecurityActorKind.Agent, ActorPseudonym.For(agentId)),
                target: new SecurityEventTarget(SecurityTargetKind.Tool, toolName),
                severity: decision == SecurityPolicyDecision.Deny
                    ? SecurityEventSeverity.Medium
                    : SecurityEventSeverity.Info);
            _securityEvents.Record(evt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record tool policy security event for action {Action}.", action);
        }
    }
}

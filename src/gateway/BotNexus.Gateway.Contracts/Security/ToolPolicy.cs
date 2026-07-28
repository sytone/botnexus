namespace BotNexus.Gateway.Abstractions.Security;

/// <summary>
/// Classifies the risk level of a tool invocation.
/// </summary>
public enum ToolRiskLevel
{
    /// <summary>Tool is safe and requires no special handling.</summary>
    Safe,

    /// <summary>Tool has moderate risk — logged but not blocked.</summary>
    Moderate,

    /// <summary>Tool is dangerous and requires explicit approval.</summary>
    Dangerous
}

/// <summary>
/// The posture applied when a tool requires approval but no approval workflow can service the
/// request (issue #2391).
/// </summary>
/// <remarks>
/// <para>
/// BotNexus has no interactive tool-approval workflow at the <c>BeforeToolCall</c> seam: the hook
/// event carries no conversation identity, so it cannot route to the <c>ask_user</c> gateway seam
/// (<c>IAskUserPromptResolver</c>, #2322/#2334). Until it can, the only two postures that can be
/// enforced honestly at this boundary are <see cref="Allow"/> and <see cref="Deny"/>.
/// </para>
/// <para>
/// <see cref="Allow"/> is the default and reproduces the historical behaviour exactly: approval is
/// required in principle, no workflow exists, execution proceeds with an audit record. This keeps
/// unattended and headless operation working. <see cref="Deny"/> is the opt-in fail-closed posture:
/// the call is refused with <c>ask-fallback-deny</c> rather than allowed by omission.
/// </para>
/// </remarks>
public enum ToolApprovalFallback
{
    /// <summary>
    /// Permit execution when approval is required but unobtainable. Default; preserves the
    /// behaviour that unattended agents and sub-agents depend on.
    /// </summary>
    Allow,

    /// <summary>
    /// Refuse execution when approval is required but unobtainable. Fail-closed posture.
    /// </summary>
    Deny
}

/// <summary>
/// Describes a tool's risk classification and approval requirements.
/// </summary>
public sealed record ToolPolicyEntry(string ToolName, ToolRiskLevel RiskLevel, bool RequiresApproval);

/// <summary>
/// Provides risk classification and approval requirements for tools.
/// Used by hook handlers to enforce tool-level security policies.
/// </summary>
public interface IToolPolicyProvider
{
    /// <summary>Returns the risk level for a given tool.</summary>
    ToolRiskLevel GetRiskLevel(string toolName);

    /// <summary>
    /// Returns whether the given tool requires explicit approval before execution.
    /// Per-agent overrides may relax or tighten the default policy.
    /// </summary>
    bool RequiresApproval(string toolName, string? agentId = null);

    /// <summary>
    /// Returns the posture to apply when <see cref="RequiresApproval"/> is <c>true</c> for the tool
    /// but no approval workflow can service the request. Defaults to
    /// <see cref="ToolApprovalFallback.Allow"/> so existing deployments are unchanged; an explicit
    /// per-agent or platform-wide configuration selects <see cref="ToolApprovalFallback.Deny"/>.
    /// </summary>
    ToolApprovalFallback GetApprovalFallback(string toolName, string? agentId = null);

    /// <summary>Returns tool names that are blocked from the HTTP API surface.</summary>
    IReadOnlyList<string> GetDeniedForHttp();
}

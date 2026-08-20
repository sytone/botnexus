using BotNexus.Domain.Primitives;

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

    /// <summary>
    /// Returns whether <paramref name="toolName"/> is actually callable by <paramref name="agentId"/>
    /// this turn, given the descriptor's resolved tool allowlist (#3468).
    /// </summary>
    /// <param name="toolName">The tool being tested.</param>
    /// <param name="agentId">The agent whose effective policy applies; <see langword="null"/> means unscoped.</param>
    /// <param name="allowedToolIds">
    /// The descriptor's resolved tool allowlist. <c>null</c>, empty, or the single wildcard entry
    /// <c>*</c> all mean "no allowlist restriction", matching the isolation strategy's own reading
    /// of <c>ToolIds</c> - so an archetype-restricted sub-agent and an unrestricted one are
    /// distinguished here exactly as they are when the tool set is actually assembled.
    /// </param>
    /// <remarks>
    /// The default implementation answers from the allowlist alone. It exists so that callers
    /// outside gateway security - and the several test doubles that implement this interface -
    /// need not know about deny-lists or runtime pinning; the production implementation overrides
    /// it to consult both.
    /// </remarks>
    bool IsToolAvailable(string toolName, AgentId? agentId = null, IReadOnlyList<string>? allowedToolIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return IsUnrestrictedAllowList(allowedToolIds)
               || allowedToolIds!.Contains(toolName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>null</c> / empty / <c>["*"]</c> all denote "every tool", mirroring
    /// <c>InProcessIsolationStrategy.IsWildcardToolIds</c>. Kept on the interface so the
    /// production implementation and the default implementation cannot drift apart on what an
    /// unrestricted descriptor looks like.
    /// </summary>
    static bool IsUnrestrictedAllowList(IReadOnlyList<string>? allowedToolIds)
        => allowedToolIds is null
           || allowedToolIds.Count == 0
           || (allowedToolIds.Count == 1 && allowedToolIds[0] == "*");
}

using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// The one rule deciding whether an agent may reach another agent.
/// </summary>
/// <remarks>
/// <para>
/// The operator states this policy once, under <c>gateway:agentExchange:accessPolicy</c>: <c>open</c>
/// (the default - any agent may reach any other) or <c>whitelist</c> (the initiator must have the
/// target in its <c>subAgents</c> list, or hold a matching <c>subAgentRoles</c> grant).
/// </para>
/// <para>
/// It was enforced on ONE of the two paths that reach another agent. <c>agent_converse</c> checked
/// it; <c>spawn_subagent</c> with a <c>targetAgentId</c> did not, and that path is the stronger of
/// the two - a Mirror spawn runs the TARGET's descriptor verbatim, so the child holds the target's
/// tools, not the parent's. The tool-grant escalation guard does not cover it either: that guard
/// inspects the tools a spawn REQUESTS, and a Mirror spawn requests none, taking them wholesale
/// from the target instead.
/// </para>
/// <para>
/// So an operator who had written <c>whitelist</c> - who had stated the policy - was getting it
/// applied to conversation and ignored on delegation. This type is the shared implementation, so
/// there is one rule rather than two copies of it that can drift.
/// </para>
/// <para>
/// Only MIRROR spawns are subject to it. An Embody spawn clones the PARENT's own descriptor and
/// reaches no other agent, so there is no grant to check and nothing to escalate to.
/// </para>
/// </remarks>
public static class DelegationPolicy
{
    /// <summary>
    /// Whether <paramref name="initiator"/> is permitted to act through <paramref name="target"/>.
    /// </summary>
    /// <param name="initiator">The agent asking to converse with, or mirror, another.</param>
    /// <param name="targetId">The requested target's id.</param>
    /// <param name="target">
    /// The target's descriptor when it is registered locally; <see langword="null"/> for a remote
    /// target, whose role cannot be read and so cannot satisfy a role grant.
    /// </param>
    /// <returns><see langword="true"/> when the grant exists.</returns>
    public static bool IsGranted(AgentDescriptor initiator, string targetId, AgentDescriptor? target)
    {
        ArgumentNullException.ThrowIfNull(initiator);

        return initiator.SubAgentIds.Contains(targetId, StringComparer.OrdinalIgnoreCase)
               || IsRoleGranted(initiator, target);
    }

    /// <summary>
    /// Whether the target carries a <c>role</c> the initiator was granted.
    /// </summary>
    /// <remarks>
    /// Role grants are how an operator says "may delegate to any reviewer" without naming each
    /// one. The role lives in the TARGET's metadata, so a remote agent - whose descriptor is not
    /// resolvable here - can never satisfy one; that is deliberate, since a role asserted by a
    /// gateway we do not control is not a grant this gateway made.
    /// </remarks>
    private static bool IsRoleGranted(AgentDescriptor initiator, AgentDescriptor? target)
    {
        if (initiator.SubAgentRoles.Count == 0 || target is null)
            return false;

        if (!target.Metadata.TryGetValue("role", out var roleRaw) || roleRaw is null)
            return false;

        var targetRole = roleRaw is System.Text.Json.JsonElement element
            ? element.GetString()
            : roleRaw.ToString();

        return !string.IsNullOrWhiteSpace(targetRole)
               && initiator.SubAgentRoles.Contains(targetRole, StringComparer.OrdinalIgnoreCase);
    }
}

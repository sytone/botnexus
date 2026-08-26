using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Keeps one agent's outbound webhook registration in sync with the agent registry.
/// </summary>
/// <remarks>
/// <para>
/// #3523: before this, a per-agent webhook callback was wired by hand with a setup script. An
/// agent created through the portal or the REST API got no registration at all, with no error
/// and no warning - the capability was simply absent until an operator remembered the script.
/// This is the provisioner pattern already used by <c>IHeartbeatProvisioner</c> and
/// <c>ISkillReviewProvisioner</c>: keep an external per-agent resource in sync with the
/// registry, idempotently, at startup and on every mutation.
/// </para>
/// <para>
/// The agent lifecycle event bus was NOT used. <c>WorldEventTypes.AgentRegistered</c> has zero
/// publishers, and <c>IActivityBroadcaster</c> is a best-effort UI feed that swallows exceptions
/// - the wrong reliability class for provisioning a shared secret.
/// </para>
/// </remarks>
public interface IAgentWebhookProvisioner
{
    /// <summary>
    /// Ensures a webhook registration exists for <paramref name="descriptor"/> and pushes the
    /// current binding to the configured delivery target.
    /// </summary>
    /// <remarks>
    /// Create-or-leave-alone, never rotate. When a registration already exists the store is not
    /// written to at all and the EXISTING secret is re-sent, so a display-name change cannot
    /// re-key a binding a downstream system already holds. Rotation is a separate capability.
    /// </remarks>
    Task ProvisionAsync(AgentDescriptor descriptor, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the webhook registrations this provisioner owns for <paramref name="agentId"/>
    /// and tells the delivery target the agent is gone.
    /// </summary>
    Task DeprovisionAsync(AgentId agentId, CancellationToken cancellationToken);
}

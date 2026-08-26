using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Contracts.Webhooks;

/// <summary>
/// One agent's outbound webhook binding, as handed to a delivery target.
/// </summary>
/// <param name="AgentId">Immutable agent identity. Stable across display-name changes.</param>
/// <param name="DisplayName">Current human-readable name, which MAY change between calls.</param>
/// <param name="WebhookId">Identifier of the BotNexus registration backing this binding.</param>
/// <param name="InboundPath">
/// Gateway-relative path an external system POSTs to, e.g. <c>/api/webhooks/agent-a/wh_abc</c>.
/// Relative because the gateway does not reliably know its own externally-visible origin;
/// a delivery target that needs an absolute URL composes one from its own configured base.
/// </param>
/// <param name="Secret">
/// The HMAC secret for this registration. This is the EXISTING secret whenever a registration
/// already exists - provisioning never re-keys, because a new secret would silently break the
/// binding already held by the downstream system.
/// </param>
public sealed record AgentWebhookBinding(
    AgentId AgentId,
    string DisplayName,
    string WebhookId,
    string InboundPath,
    string Secret);

/// <summary>
/// Receives per-agent webhook bindings so a downstream system can be kept in sync with the
/// BotNexus agent registry.
/// </summary>
/// <remarks>
/// <para>
/// This contract deliberately lives in <c>BotNexus.Gateway.Contracts</c> and names no product:
/// core must not take an outbound dependency on any one downstream consumer. Implementations
/// ship as extension assemblies under <c>src/extensions/</c> and are discovered by the
/// assembly-load-context extension loader.
/// </para>
/// <para>
/// Implementations MUST be inert when unconfigured - no outbound call, no throw - so a gateway
/// with no downstream target starts and runs exactly as before.
/// </para>
/// </remarks>
public interface IAgentWebhookTargetNotifier
{
    /// <summary>Pushes the current binding for one agent to the downstream target.</summary>
    Task NotifyAsync(AgentWebhookBinding binding, CancellationToken cancellationToken);

    /// <summary>
    /// Tells the downstream target that one specific provisioner-owned binding has been removed.
    /// </summary>
    /// <param name="agentId">The agent whose binding was removed.</param>
    /// <param name="webhookId">
    /// The exact registration being removed. This is the generation token that makes the delete
    /// safe to replay: a downstream target MUST condition its delete on
    /// <c>agent = agentId AND webhookId = webhookId</c> and treat a mismatch as an idempotent
    /// no-op. Without it, a delayed or retried DELETE for a since-deleted agent erases the NEWER
    /// binding of a recreated agent with the same id - agent ids are immutable and therefore
    /// reusable, so agent id alone is not a safe delete key.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Invoked once per owned registration. An agent with no provisioner-owned registration
    /// produces no call at all, because there is no binding whose removal could be described.
    /// </remarks>
    Task NotifyRemovedAsync(AgentId agentId, string webhookId, CancellationToken cancellationToken);
}

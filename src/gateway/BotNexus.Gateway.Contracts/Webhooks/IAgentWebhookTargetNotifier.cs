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

    /// <summary>Tells the downstream target that <paramref name="agentId"/> no longer exists.</summary>
    Task NotifyRemovedAsync(AgentId agentId, CancellationToken cancellationToken);
}

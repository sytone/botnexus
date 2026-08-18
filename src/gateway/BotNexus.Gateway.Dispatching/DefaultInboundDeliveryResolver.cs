using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Default <see cref="IInboundDeliveryResolver"/>. Reads the caller's intent from the message's
/// routing hints, reads whether a turn is actually running from <see cref="IAgentSupervisor"/>, and
/// collapses the two into the mechanism the orchestrator will use (#3028 AC1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Auto queues.</b> <see cref="InboundDeliveryMode.Auto"/> resolves to
/// <see cref="InboundDeliveryMode.Queue"/> whether or not a turn is running. This is the documented
/// default and it is deliberately conservative: every pre-#3028 caller reached the orchestrator with
/// no expressed intent, so making Auto steer would retroactively change the meaning of the webhook
/// path, the channel adapters and the conversation-messages endpoint all at once. Steering is opt-in.
/// </para>
/// <para>
/// <b>Steer and Interrupt degrade rather than dead-letter.</b> A steer only has meaning against a
/// turn in flight; injecting into an idle agent's pending queue produces a message that is never
/// drained, because the loop that would read it has already ended. The SignalR hub learned this the
/// hard way and guards against it explicitly. Rather than repeat that guard at every future call
/// site, this resolver reports <see cref="InboundDeliveryMode.Queue"/> when nothing is running, and
/// <see cref="InboundDeliveryDecision.FellBackToQueue"/> stays <see langword="true"/> so the
/// downgrade is observable instead of silent.
/// </para>
/// <para>
/// <b>A message with no addressed session can only queue.</b> Steering targets one live handle; with
/// no <see cref="InboundMessageRoutingHints.RequestedSessionId"/> there is no handle to look up, and
/// guessing one would be exactly the client-side mis-route this issue exists to remove.
/// </para>
/// </remarks>
/// <param name="supervisor">
/// Supervisor consulted for a LIVE handle. <see cref="IAgentSupervisor.GetHandle"/> is used, never
/// <c>GetOrCreateAsync</c>: this seam must answer "is a turn running?" without conjuring an idle
/// handle as a side effect of asking.
/// </param>
public sealed class DefaultInboundDeliveryResolver(IAgentSupervisor supervisor) : IInboundDeliveryResolver
{
    /// <inheritdoc />
    public Task<InboundDeliveryDecision> ResolveAsync(
        InboundMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var hints = InboundMessageRoutingHints.FromMessage(message);
        var requested = hints.DeliveryMode;

        // Auto never inspects the turn: its answer is Queue either way. Short-circuiting keeps the
        // overwhelmingly common path free of a supervisor lookup.
        if (requested is InboundDeliveryMode.Auto or InboundDeliveryMode.Queue)
        {
            return Task.FromResult(new InboundDeliveryDecision(
                requested, InboundDeliveryMode.Queue, TurnWasActive: false));
        }

        var turnActive = IsTurnActive(hints);
        var resolved = turnActive ? requested : InboundDeliveryMode.Queue;
        return Task.FromResult(new InboundDeliveryDecision(requested, resolved, turnActive));
    }

    /// <summary>
    /// Reports whether the addressed session currently has a running agent handle. Returns
    /// <see langword="false"/> when the message does not name both an agent and a session, because
    /// there is then no single handle the steer could target.
    /// </summary>
    private bool IsTurnActive(InboundMessageRoutingHints hints)
    {
        if (hints.RequestedAgentId is not { } agentId || hints.RequestedSessionId is not { } sessionId)
        {
            return false;
        }

        return supervisor.GetHandle(agentId, sessionId) is { IsRunning: true };
    }
}

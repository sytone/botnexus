using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Injects an inbound message into a turn already in flight, implementing the delivery half of the
/// #3028 seam. Lives in <c>BotNexus.Gateway</c> because it needs the agent handle and the session
/// store; <c>BotNexus.Gateway.Dispatching</c> owns only the <see cref="IInboundSteerDeliverer"/>
/// contract.
/// </summary>
/// <remarks>
/// <para>
/// The injection sequence mirrors the SignalR hub's <c>SteerWithMedia</c> exactly, because the whole
/// point of this issue is that the hub and every other surface must produce the same result for the
/// same intent: re-check that the handle is genuinely running, persist the message to session
/// history so the transcript records what the agent was told, then inject.
/// </para>
/// <para>
/// <b>The running re-check is not redundant.</b> <see cref="IInboundDeliveryResolver"/> read
/// <c>IsRunning</c> at decision time; the turn may have ended since. Injecting into a handle that has
/// stopped puts a message into a <c>PendingMessageQueue</c> nothing will ever drain — a silent loss.
/// Returning <see langword="false"/> instead sends the message back to the orchestrator's queue,
/// where it starts a fresh turn.
/// </para>
/// <para>
/// <b>History is written only after the running re-check passes</b>, so a message that ends up
/// queued is not recorded twice: once here and once by the normal processing path.
/// </para>
/// </remarks>
/// <param name="supervisor">Supervisor used to look up the LIVE handle, never to create one.</param>
/// <param name="sessions">Session store, for persisting the injected message into history.</param>
/// <param name="logger">Logger.</param>
public sealed class AgentHandleSteerDeliverer(
    IAgentSupervisor supervisor,
    ISessionStore sessions,
    ILogger<AgentHandleSteerDeliverer> logger) : IInboundSteerDeliverer
{
    /// <inheritdoc />
    public async Task<bool> TryDeliverAsync(
        InboundMessage message,
        InboundDeliveryDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(decision);

        var hints = InboundMessageRoutingHints.FromMessage(message);
        if (hints.RequestedAgentId is not { } agentId || hints.RequestedSessionId is not { } sessionId)
        {
            // Without both ids there is no single handle to steer. The resolver should already have
            // returned Queue here, so reaching this point means the caller bypassed it — refuse
            // rather than guess a target.
            return false;
        }

        var handle = supervisor.GetHandle(agentId, sessionId);
        if (handle is null || !handle.IsRunning)
        {
            logger.LogInformation(
                "Steer target for agent '{AgentId}' session '{SessionId}' is no longer running; deferring to the queue.",
                agentId.Value, sessionId.Value);
            return false;
        }

        var session = await sessions.GetOrCreateAsync(sessionId, agentId, cancellationToken);
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.User,
            Content = message.Content,
            SenderId = message.SenderId
        });
        await sessions.SaveAsync(session, cancellationToken);

        if (decision.Resolved == InboundDeliveryMode.Interrupt)
        {
            await handle.InterruptAndSteerAsync(message.Content, cancellationToken);
        }
        else
        {
            await handle.SteerAsync(message.Content, cancellationToken);
        }

        logger.LogInformation(
            "Injected {Mode} into running turn for agent '{AgentId}' session '{SessionId}'.",
            decision.Resolved, agentId.Value, sessionId.Value);

        return true;
    }
}

using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;
using AgentId = BotNexus.Domain.Primitives.AgentId;
using ConversationId = BotNexus.Domain.Primitives.ConversationId;
using SessionId = BotNexus.Domain.Primitives.SessionId;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// Application-service facade that collapses the gateway's inbound-dispatch, warmup,
/// conversation-resolution, compaction, and conversation-reset collaborators behind a
/// single dependency so <see cref="GatewayHub"/> stays a thin transport adapter.
/// </summary>
/// <remarks>
/// <para>
/// The hub previously injected these five gateway operations individually
/// (<c>IInboundMessageOrchestrator</c>, <c>ISessionWarmupService</c>,
/// <c>IConversationDispatcher</c>, <c>ISessionCompactionCoordinator</c>, and the optional
/// <c>IConversationResetService</c>). Grouping them here shrinks the hub constructor to the
/// SignalR-context-bound services it genuinely coordinates (supervisor, session store,
/// activity broadcaster, conversation router/store, ask-user registry) and gives the
/// control methods one shared application boundary.
/// </para>
/// <para>
/// This is a pure pass-through boundary: every member forwards to the underlying gateway
/// collaborator with identical semantics. It carries no SignalR context and performs no
/// routing policy of its own, so hub control methods remain individually testable by
/// substituting the underlying collaborators when the facade is constructed.
/// </para>
/// <para>
/// The interface is public because it appears in the public <see cref="GatewayHub"/>
/// constructor signature; the default implementation stays internal and is composed in DI.
/// </para>
/// </remarks>
public interface IGatewayHubApplicationService
{
    /// <summary>
    /// Enqueues an inbound message on its per-session queue via the gateway orchestrator and
    /// awaits the processing outcome. Forwards to <see cref="IInboundMessageOrchestrator.AcceptAsync"/>.
    /// </summary>
    Task<InboundDispatchResult> AcceptAsync(InboundMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the sessions currently available to the caller for UI initialisation at
    /// subscribe time. Forwards to <see cref="ISessionWarmupService.GetAvailableSessionsAsync(CancellationToken)"/>.
    /// </summary>
    Task<IReadOnlyList<SessionSummary>> GetAvailableSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the conversation and session an inbound message will land on so the hub can
    /// join the caller connection to the conversation group and populate its synchronous
    /// result contract. Forwards to <see cref="IConversationDispatcher.DispatchAsync"/>.
    /// </summary>
    Task<DispatchResult> ResolveSessionAsync(InboundMessageContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the full compaction pipeline for a session (force-compaction from the portal
    /// <c>/compact</c> RPC). Forwards to <see cref="ISessionCompactionCoordinator.CompactAsync"/>.
    /// </summary>
    Task<SessionCompactionOutcome> CompactAsync(
        AgentId agentId,
        GatewaySession session,
        CancellationToken cancellationToken,
        bool force = false);

    /// <summary>
    /// Resets the conversation's active session (stop agent, flush session-end memory, cancel
    /// pending ask-user waits, seal the session, clear the active session pointer) when a
    /// conversation-reset service is configured. Returns <see langword="false"/> when reset is
    /// not available so the hub falls back to sealing an orphan session in place.
    /// </summary>
    /// <param name="conversationId">The conversation whose active session is being reset.</param>
    /// <param name="expectedActiveSessionId">Guard against stale caller session ids; the reset
    /// only proceeds when it matches the conversation's current active session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a conversation-reset service handled the reset;
    /// <see langword="false"/> when none is configured.</returns>
    Task<bool> TryResetActiveSessionAsync(
        ConversationId conversationId,
        SessionId? expectedActiveSessionId,
        CancellationToken cancellationToken);
}

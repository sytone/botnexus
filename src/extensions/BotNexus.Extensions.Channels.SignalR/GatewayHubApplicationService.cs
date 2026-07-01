using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;
using AgentId = BotNexus.Domain.Primitives.AgentId;
using ConversationId = BotNexus.Domain.Primitives.ConversationId;
using SessionId = BotNexus.Domain.Primitives.SessionId;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// Default <see cref="IGatewayHubApplicationService"/> that forwards each hub-facing gateway
/// operation to the concrete collaborator. Holds no state and no SignalR context, so it is a
/// singleton composed once from the gateway's singleton collaborators.
/// </summary>
internal sealed class GatewayHubApplicationService : IGatewayHubApplicationService
{
    private readonly IInboundMessageOrchestrator _orchestrator;
    private readonly ISessionWarmupService _warmup;
    private readonly IConversationDispatcher _conversationDispatcher;
    private readonly ISessionCompactionCoordinator _compactionCoordinator;
    private readonly IConversationResetService? _resetService;

    /// <summary>
    /// Composes the facade from the gateway's inbound-dispatch, warmup, conversation-resolution,
    /// compaction, and (optional) conversation-reset collaborators.
    /// </summary>
    /// <param name="orchestrator">Single inbound entry point for injecting messages into the gateway.</param>
    /// <param name="warmup">Provides the sessions available to a connection at subscribe time.</param>
    /// <param name="conversationDispatcher">Resolves conversation/session targets for inbound messages.</param>
    /// <param name="compactionCoordinator">Runs the full session-compaction pipeline.</param>
    /// <param name="resetService">Canonical conversation active-session reset; <see langword="null"/>
    /// when the host does not register one, in which case the hub seals orphan sessions in place.</param>
    public GatewayHubApplicationService(
        IInboundMessageOrchestrator orchestrator,
        ISessionWarmupService warmup,
        IConversationDispatcher conversationDispatcher,
        ISessionCompactionCoordinator compactionCoordinator,
        IConversationResetService? resetService = null)
    {
        _orchestrator = orchestrator;
        _warmup = warmup;
        _conversationDispatcher = conversationDispatcher;
        _compactionCoordinator = compactionCoordinator;
        _resetService = resetService;
    }

    /// <inheritdoc />
    public Task<InboundDispatchResult> AcceptAsync(InboundMessage message, CancellationToken cancellationToken = default)
        => _orchestrator.AcceptAsync(message, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionSummary>> GetAvailableSessionsAsync(CancellationToken cancellationToken = default)
        => _warmup.GetAvailableSessionsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<DispatchResult> ResolveSessionAsync(InboundMessageContext context, CancellationToken cancellationToken = default)
        => _conversationDispatcher.DispatchAsync(context, cancellationToken);

    /// <inheritdoc />
    public Task<SessionCompactionOutcome> CompactAsync(
        AgentId agentId,
        GatewaySession session,
        CancellationToken cancellationToken,
        bool force = false)
        => _compactionCoordinator.CompactAsync(agentId, session, cancellationToken, force);

    /// <inheritdoc />
    public async Task<bool> TryResetActiveSessionAsync(
        ConversationId conversationId,
        SessionId? expectedActiveSessionId,
        CancellationToken cancellationToken)
    {
        if (_resetService is null)
            return false;

        await _resetService.ResetActiveSessionAsync(conversationId, expectedActiveSessionId, cancellationToken);
        return true;
    }
}

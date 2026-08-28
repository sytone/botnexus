using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ILogger<GatewayHubApplicationService> _logger;

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
    /// <param name="logger">Records the inbound boundary diagnostic (#3600). Optional so the many
    /// direct-construction sites keep working; defaults to a no-op logger.</param>
    public GatewayHubApplicationService(
        IInboundMessageOrchestrator orchestrator,
        ISessionWarmupService warmup,
        IConversationDispatcher conversationDispatcher,
        ISessionCompactionCoordinator compactionCoordinator,
        IConversationResetService? resetService = null,
        ILogger<GatewayHubApplicationService>? logger = null)
    {
        _orchestrator = orchestrator;
        _warmup = warmup;
        _conversationDispatcher = conversationDispatcher;
        _compactionCoordinator = compactionCoordinator;
        _resetService = resetService;
        _logger = logger ?? NullLogger<GatewayHubApplicationService>.Instance;
    }

    /// <inheritdoc />
    /// <remarks>
    /// #3600 closed the observability gap between <c>Hub SendMessage</c> and
    /// <c>GatewayHost.ProcessAsync</c>. Previously this method was a bare forward, so a message that
    /// never reached the host produced no line of its own and the drop was invisible. Every inbound
    /// message now records its resolved <see cref="InboundIsolationKey"/> and terminal
    /// <see cref="InboundDispatchStatus"/> here — at Warning when the outcome is neither
    /// <c>Accepted</c> nor <c>Steered</c>, so a stalled or refused message is never silent.
    /// </remarks>
    public async Task<InboundDispatchResult> AcceptAsync(
        InboundMessage message,
        CancellationToken cancellationToken = default)
    {
        var isolationKey = InboundIsolationKey.ForMessage(message);
        var result = await _orchestrator.AcceptAsync(message, cancellationToken);

        var level = result.Status is InboundDispatchStatus.Accepted or InboundDispatchStatus.Steered
            ? LogLevel.Debug
            : LogLevel.Warning;

        _logger.Log(level,
            "Inbound accept for isolation unit '{IsolationKey}' (scope {IsolationScope}) on channel " +
            "'{ChannelType}' resolved to status {Status} with {DispatchCount} dispatch(es).",
            isolationKey.Value,
            isolationKey.Scope,
            message.ChannelType,
            result.Status,
            result.Dispatches.Count);

        return result;
    }

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

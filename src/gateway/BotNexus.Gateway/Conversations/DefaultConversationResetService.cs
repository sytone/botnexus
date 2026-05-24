using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Conversations;

/// <summary>
/// Canonical implementation of <see cref="IConversationResetService"/>.
/// </summary>
/// <remarks>
/// <para>This is the single owner of the reset sequence shared by REST
/// (<c>ConversationsController.Archive</c> and <c>POST .../reset</c>) and SignalR
/// (<c>GatewayHub.ResetSession</c>). The architecture test
/// <c>NoDirect_ISessionEndMemoryFlusher_FlushAsync_OutsideAllowlist</c> fences against
/// other code regressing into bypassing this service to call
/// <see cref="ISessionEndMemoryFlusher.FlushAsync"/> directly.</para>
///
/// <para>The step ordering is deliberate:</para>
/// <list type="number">
///   <item><b>Stop supervisor first.</b> Quiesces any in-flight agent turn for the session
///         so the subsequent flush isn't racing the current handle.</item>
///   <item><b>Flush memory next.</b> The flusher spawns a separate internal-trigger session
///         (<c>SessionEndMemoryFlusher</c>), so stopping the original handle does not
///         interfere with its execution.</item>
///   <item><b>Cancel pending <c>ask_user</c> waits.</b> Otherwise the next inbound for this
///         conversation is consumed by <c>PendingAskUserInterceptor</c> as a stale response.</item>
///   <item><b>Seal the session (Status = Sealed; SaveAsync).</b> Deliberately not
///         <c>ISessionStore.ArchiveAsync</c>: <c>InMemorySessionStore.ArchiveAsync</c> deletes
///         the row outright; <c>FileSessionStore.ArchiveAsync</c> renames the file out of the
///         normal lookup directory. Sealing preserves history for transcript readers.</item>
///   <item><b>Clear <see cref="Conversation.ActiveSessionId"/>.</b> The router's resolution
///         path creates a fresh session on the next inbound when <c>ActiveSessionId</c> is
///         null; the new session starts with empty history, which is exactly the signal the
///         Phase 3d invariant uses to re-initialise the system prompt.</item>
/// </list>
/// </remarks>
internal sealed class DefaultConversationResetService : IConversationResetService
{
    private readonly IConversationStore _conversations;
    private readonly ISessionStore _sessions;
    private readonly IAgentSupervisor _supervisor;
    private readonly ISessionEndMemoryFlusher? _sessionEndFlusher;
    private readonly IAskUserResponseRegistry? _askUserResponseRegistry;
    private readonly IOptionsMonitor<CompactionOptions> _compactionOptions;
    private readonly ILogger<DefaultConversationResetService> _logger;
    private readonly TimeProvider _timeProvider;

    public DefaultConversationResetService(
        IConversationStore conversations,
        ISessionStore sessions,
        IAgentSupervisor supervisor,
        IOptionsMonitor<CompactionOptions> compactionOptions,
        ILogger<DefaultConversationResetService> logger,
        ISessionEndMemoryFlusher? sessionEndFlusher = null,
        IAskUserResponseRegistry? askUserResponseRegistry = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(compactionOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _conversations = conversations;
        _sessions = sessions;
        _supervisor = supervisor;
        _sessionEndFlusher = sessionEndFlusher;
        _askUserResponseRegistry = askUserResponseRegistry;
        _compactionOptions = compactionOptions;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ConversationResetResult> ResetActiveSessionAsync(
        ConversationId conversationId,
        SessionId? expectedActiveSessionId = null,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
        {
            _logger.LogDebug("Reset requested for unknown conversation {ConversationId}; returning NotFound.", conversationId);
            return new ConversationResetResult(ConversationResetOutcome.NotFound, SealedSessionId: null, AgentId: null);
        }

        if (conversation.ActiveSessionId is not { } activeSessionId)
        {
            _logger.LogDebug("Reset requested for conversation {ConversationId} with no active session; no-op.", conversationId);
            return new ConversationResetResult(ConversationResetOutcome.NoActiveSession, SealedSessionId: null, AgentId: conversation.AgentId);
        }

        if (expectedActiveSessionId is { } expected && expected != activeSessionId)
        {
            _logger.LogInformation(
                "Reset for conversation {ConversationId} requested with stale session id {Expected}; current ActiveSessionId is {Actual}. Leaving current session untouched.",
                conversationId, expected, activeSessionId);
            return new ConversationResetResult(ConversationResetOutcome.StaleSessionId, SealedSessionId: null, AgentId: conversation.AgentId);
        }

        var agentId = conversation.AgentId;
        var gatewaySession = await _sessions.GetAsync(activeSessionId, cancellationToken).ConfigureAwait(false);
        if (gatewaySession is null)
        {
            _logger.LogWarning(
                "Conversation {ConversationId} pointed at session {SessionId} which is no longer in the store. Clearing ActiveSessionId defensively.",
                conversationId, activeSessionId);
            conversation.ActiveSessionId = null;
            conversation.UpdatedAt = _timeProvider.GetUtcNow();
            await _conversations.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);
            return new ConversationResetResult(ConversationResetOutcome.NoActiveSession, SealedSessionId: null, AgentId: agentId);
        }

        // Step 1: stop the supervisor handle for this session (idempotent — safe if not running).
        try
        {
            await _supervisor.StopAsync(agentId, activeSessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Supervisor stop failed for session {SessionId}; reset will proceed.",
                activeSessionId);
        }

        // Step 2: best-effort session-end memory flush. Skipped for non-interactive sessions
        // via ShouldFlush. FlushAsync swallows its own exceptions, but we wrap defensively.
        if (_sessionEndFlusher is not null)
        {
            var compactionOptions = _compactionOptions.CurrentValue;
            if (_sessionEndFlusher.ShouldFlush(gatewaySession.Session, compactionOptions))
            {
                try
                {
                    await _sessionEndFlusher.FlushAsync(agentId, gatewaySession.Session, compactionOptions, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Session-end memory flush failed for session {SessionId}; reset will proceed.",
                        activeSessionId);
                }
            }
        }

        // Step 3: cancel pending ask_user waits so the next inbound is not consumed as a stale response.
        try
        {
            _askUserResponseRegistry?.CancelAllForConversation(conversationId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Cancelling pending ask_user waits failed for conversation {ConversationId}; reset will proceed.",
                conversationId);
        }

        // Step 4: seal the session (Status = Sealed; SaveAsync). Not ArchiveAsync — see class docs.
        gatewaySession.Session.Status = SessionStatus.Sealed;
        gatewaySession.Session.UpdatedAt = _timeProvider.GetUtcNow();
        await _sessions.SaveAsync(gatewaySession, cancellationToken).ConfigureAwait(false);

        // Step 5: clear ActiveSessionId. The router creates a fresh session on next inbound.
        conversation.ActiveSessionId = null;
        conversation.UpdatedAt = _timeProvider.GetUtcNow();
        await _conversations.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Reset conversation {ConversationId}: sealed session {SessionId}, cleared ActiveSessionId.",
            conversationId, activeSessionId);

        return new ConversationResetResult(ConversationResetOutcome.Reset, SealedSessionId: activeSessionId, AgentId: agentId);
    }
}

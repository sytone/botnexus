using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Conversations;

/// <summary>
/// Outcome of a <see cref="IConversationResetService.ResetActiveSessionAsync"/> call.
/// </summary>
public enum ConversationResetOutcome
{
    /// <summary>The active session was sealed and (where applicable) memory was flushed
    /// and pending <c>ask_user</c> waits were cancelled.</summary>
    Reset,

    /// <summary>The conversation exists but has no <see cref="Conversation.ActiveSessionId"/>;
    /// nothing to reset. Treated as a successful no-op by callers.</summary>
    NoActiveSession,

    /// <summary>The conversation was not found in the store.</summary>
    NotFound,

    /// <summary>The caller passed an <c>expectedActiveSessionId</c> that does not match the
    /// conversation's current <see cref="Conversation.ActiveSessionId"/>; the current session
    /// is intentionally left untouched. This protects channels that hold a stale session id
    /// (e.g. SignalR clients) from clobbering a newer session that was created in the meantime.</summary>
    StaleSessionId,
}

/// <summary>
/// Result of a conversation reset attempt.
/// </summary>
/// <param name="Outcome">The classification of what happened.</param>
/// <param name="SealedSessionId">The session id that was sealed when <see cref="Outcome"/>
/// is <see cref="ConversationResetOutcome.Reset"/>; otherwise <c>null</c>.</param>
/// <param name="AgentId">The agent that owned the sealed session, for caller convenience
/// (e.g. emitting <c>SessionReset</c> notifications) when <see cref="Outcome"/> is
/// <see cref="ConversationResetOutcome.Reset"/>; otherwise <c>null</c>.</param>
public sealed record ConversationResetResult(
    ConversationResetOutcome Outcome,
    SessionId? SealedSessionId,
    AgentId? AgentId);

/// <summary>
/// Canonical "reset the active session of a conversation" operation.
/// </summary>
/// <remarks>
/// <para>This service is the single source of truth for the reset sequence shared by
/// REST (<c>ConversationsController</c>: both <c>DELETE</c>/archive and <c>POST .../reset</c>)
/// and SignalR (<c>GatewayHub.ResetSession</c>). It exists to close a historical defect
/// (REST archive used to seal sessions but never invoke
/// <see cref="Sessions.ISessionEndMemoryFlusher"/>, silently losing the session-end memory
/// bridge that SignalR users received).</para>
///
/// <para>The canonical sequence is:</para>
/// <list type="number">
///   <item>Load the conversation; emit <see cref="ConversationResetOutcome.NotFound"/> if missing.</item>
///   <item>If <see cref="Conversation.ActiveSessionId"/> is <c>null</c>, return
///         <see cref="ConversationResetOutcome.NoActiveSession"/>.</item>
///   <item>If <paramref name="expectedActiveSessionId"/> is supplied and does not match the
///         conversation's current active session, return
///         <see cref="ConversationResetOutcome.StaleSessionId"/> (do not touch the current session).</item>
///   <item>Stop the agent supervisor handle for the session.</item>
///   <item>Best-effort invoke <see cref="Sessions.ISessionEndMemoryFlusher.FlushAsync"/>
///         (skipped for non-interactive sessions; exceptions logged, not thrown).</item>
///   <item>Cancel any pending <c>ask_user</c> waits for the conversation so the next inbound
///         is not consumed by the stale <c>PendingAskUserInterceptor</c>.</item>
///   <item>Seal the session: <c>Status = Sealed</c>, <c>UpdatedAt = now</c>, persist via
///         <see cref="Sessions.ISessionStore.SaveAsync"/>. (Deliberately not
///         <c>ArchiveAsync</c>, whose semantics vary by store implementation — some delete
///         the row outright.)</item>
///   <item>Clear <see cref="Conversation.ActiveSessionId"/> and persist the conversation.
///         The next inbound message will create a fresh session via the router.</item>
/// </list>
///
/// <para>A new session is intentionally <b>not</b> created eagerly — the router materialises
/// one on the next inbound, at which point the session's empty history naturally signals
/// system-prompt re-initialisation (Phase 3d invariant).</para>
/// </remarks>
public interface IConversationResetService
{
    /// <summary>
    /// Resets the conversation's active session.
    /// </summary>
    /// <param name="conversationId">The conversation to reset.</param>
    /// <param name="expectedActiveSessionId">Optional guard: if supplied, the reset proceeds
    /// only when <see cref="Conversation.ActiveSessionId"/> matches; otherwise returns
    /// <see cref="ConversationResetOutcome.StaleSessionId"/>. Pass the caller's last-known
    /// session id (e.g. the one delivered on a SignalR <c>ResetSession</c> call) to avoid
    /// clobbering a newer session that was created by a concurrent inbound.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ConversationResetResult> ResetActiveSessionAsync(
        ConversationId conversationId,
        SessionId? expectedActiveSessionId = null,
        CancellationToken cancellationToken = default);
}

using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Captures the authoritative identity of a session at the moment a run starts, so that
/// post-run finalizer writes (turn-transcript persistence, compaction record, metadata patch)
/// can be fenced against a session that was <b>deleted or reset while the run was in flight</b>.
/// </summary>
/// <remarks>
/// <para>
/// Issue #1518. The gateway persists the completed turn <i>after</i> the agent run returns
/// (see <c>GatewayHost</c> finalizer save, <c>StreamingSessionHelper.ProcessAndSaveAsync</c>,
/// and <c>SessionCompactionCoordinator.CompactAsync</c>). If the session/conversation was
/// deleted (<see cref="ISessionStore.DeleteAsync"/>) or reset
/// (<c>DefaultConversationResetService</c> seals the row and clears
/// <see cref="Conversation.ActiveSessionId"/>) <b>during</b> the run, a stale finalizer write
/// can:
/// </para>
/// <list type="bullet">
///   <item><b>Resurrect</b> a just-deleted row - <c>SqliteSessionStore.UpsertSessionAsync</c>
///   uses an unconditional <c>INSERT ... ON CONFLICT DO UPDATE</c>, so a save after a delete
///   re-inserts the row the operator/agent intentionally removed.</item>
///   <item><b>Clobber</b> a competing reset - the in-memory session still believes it is
///   <see cref="SessionStatus.Active"/>, so the upsert reverts the persisted
///   <see cref="SessionStatus.Sealed"/> back to Active, un-sealing a reset session.</item>
///   <item><b>Rebind</b> - if the same session id has been re-pointed at a different
///   conversation, the finalizer would overwrite the fresh replacement.</item>
/// </list>
/// <para>
/// A fenced save re-reads the authoritative store immediately before writing and
/// <b>no-ops</b> (returns <see cref="SessionSaveOutcome.Rebound"/>) when the on-disk row no
/// longer matches the captured identity. The fence is opt-in: the plain
/// <see cref="ISessionStore.SaveAsync(GatewaySession, System.Threading.CancellationToken)"/>
/// overload keeps its unconditional create-or-update semantics, which the write-ahead
/// pre-run saves (user message, crash sentinel) rely on to create the row in the first place.
/// </para>
/// </remarks>
/// <param name="ExpectedSessionId">
/// The session id owned by the run, captured at run start. A save is rebound when no row with
/// this id survives, or when the surviving row has diverged (see <see cref="ExpectedConversationId"/>).
/// </param>
/// <param name="ExpectedConversationId">
/// The conversation the session belonged to at run start. A save is rebound when the on-disk
/// row's conversation id differs (an intentional rebind/rotation).
/// </param>
public readonly record struct SessionWriteFence(
    SessionId ExpectedSessionId,
    ConversationId ExpectedConversationId)
{
    /// <summary>
    /// Captures the fence identity from a live session at run start.
    /// </summary>
    /// <param name="session">The session whose identity to capture.</param>
    /// <returns>A fence pinned to the session's current id and conversation.</returns>
    public static SessionWriteFence Capture(GatewaySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new SessionWriteFence(session.SessionId, session.ConversationId);
    }
}

/// <summary>
/// Result of a fenced <see cref="ISessionStore.SaveAsync(GatewaySession, SessionWriteFence, System.Threading.CancellationToken)"/>.
/// </summary>
public enum SessionSaveOutcome
{
    /// <summary>The session was persisted normally - the fence passed.</summary>
    Persisted = 0,

    /// <summary>
    /// The write was skipped because the on-disk session no longer matches the captured run
    /// identity (it was deleted, sealed by a reset, or rebound to another conversation while
    /// the run was in flight). No row was created or modified - the finalizer must treat this
    /// as a "session-rebound" short-circuit and suppress all downstream persistence.
    /// </summary>
    Rebound = 1
}

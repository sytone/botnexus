using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Single source of truth for the post-run session-write fence decision (issue #1518).
/// Both the default <see cref="ISessionStore.SaveAsync(GatewaySession, SessionWriteFence, System.Threading.CancellationToken)"/>
/// implementation and store-specific overrides (e.g. the SQLite store, which re-reads under a
/// lock) evaluate the fence through this method so the "session-rebound" semantics cannot drift
/// between implementations.
/// </summary>
public static class SessionFenceEvaluator
{
    /// <summary>
    /// Returns <c>true</c> when a fenced finalizer write is allowed to proceed - i.e. the
    /// on-disk row still represents the same run identity that was captured at run start.
    /// Returns <c>false</c> (rebound - suppress the write) when the row was deleted, was sealed
    /// or expired by a competing reset, or was rebound to a different conversation while the run
    /// was in flight.
    /// </summary>
    /// <param name="fence">The identity captured at run start.</param>
    /// <param name="current">
    /// The session as it currently exists on disk, or <c>null</c> when the row no longer exists.
    /// </param>
    /// <returns><c>true</c> to persist; <c>false</c> to skip as rebound.</returns>
    public static bool Passes(SessionWriteFence fence, GatewaySession? current)
    {
        // (a) Row deleted mid-run: the unconditional upsert would resurrect it.
        if (current is null)
            return false;

        // (b) Rebound to a different conversation mid-run: the same session id now belongs to
        //     another conversation, so the finalizer would clobber the fresh binding. Only
        //     compare when the captured conversation id was initialised - a run that started
        //     before its conversation was stamped has nothing meaningful to compare against and
        //     must not be skipped on that basis alone (it is still guarded by (a) and (c)).
        if (fence.ExpectedConversationId.IsInitialized()
            && current.ConversationId.IsInitialized()
            && current.ConversationId != fence.ExpectedConversationId)
        {
            return false;
        }

        // (c) Sealed/expired by a competing reset mid-run: DefaultConversationResetService seals
        //     the row (Status = Sealed) and clears Conversation.ActiveSessionId. The in-memory
        //     finalizer still believes the session is Active, so an unconditional upsert would
        //     revert Sealed -> Active, un-sealing a session the user intentionally reset. Suppress.
        if (current.Status is SessionStatus.Sealed or SessionStatus.Expired)
            return false;

        return true;
    }
}

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Tracks which sessions currently have a live, in-memory agent turn executing.
/// Used by the write-time self-heal path (#2030): when an inbound message arrives for a
/// session that carries a crash sentinel but has <b>no</b> live turn, the sentinel is an
/// orphan left by a turn that died mid-flight (sub-agent bail, provider stream death, host
/// sleep, unhandled crash) and can be cleared so the session unblocks immediately - no
/// gateway restart required. If a turn <i>is</i> live, the sentinel is legitimate and must
/// be left in place.
/// </summary>
public interface ISessionTurnTracker
{
    /// <summary>
    /// Marks the session as having a live turn and returns a scope whose disposal marks it
    /// inactive again. Reentrant-safe: nested marks are reference-counted so the session is
    /// only considered inactive once every scope has been disposed.
    /// </summary>
    /// <param name="sessionId">The session whose turn is starting.</param>
    IDisposable BeginTurn(string sessionId);

    /// <summary>
    /// Returns <c>true</c> when the session currently has at least one live turn.
    /// </summary>
    /// <param name="sessionId">The session to query.</param>
    bool HasLiveTurn(string sessionId);
}

using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Stops and drains the agent run bound to a single session so a lifecycle operation that seals
/// the session cannot commit while a run is still writing to it (issue #2903).
/// </summary>
/// <remarks>
/// <para>
/// Archiving is not a pure store mutation: <c>ISessionStore.ArchiveAsync</c> rewrites the
/// session's history from the snapshot it loaded, so a run that persists after the seal either
/// resurrects a sealed session or loses its turns to the rewrite. The store therefore asks this
/// collaborator to quiesce the run <em>before</em> it commits.
/// </para>
/// <para>
/// Implementations MUST scope the fence to the exact session they are given. Draining "the
/// agent" rather than "the session" would abort unrelated conversations that happen to share an
/// agent id, which is a worse failure than the race it replaces.
/// </para>
/// <para>
/// Implementations MUST return within the supplied timeout and report
/// <see cref="SessionDrainOutcome.TimedOut"/> rather than blocking indefinitely or claiming a
/// drain they did not achieve - the caller turns that outcome into a clean failure instead of
/// sealing over live work.
/// </para>
/// </remarks>
public interface ISessionRunDrain
{
    /// <summary>
    /// Stops any run bound to <paramref name="sessionId"/> and waits for it to settle.
    /// </summary>
    /// <param name="sessionId">The exact session to fence. No other session may be affected.</param>
    /// <param name="timeout">
    /// Upper bound on how long the caller is willing to wait. A non-positive value means "do not
    /// wait" - report the run's current state immediately.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="SessionDrainOutcome.NoActiveRun"/> when nothing was running,
    /// <see cref="SessionDrainOutcome.Drained"/> when a run was stopped and settled, or
    /// <see cref="SessionDrainOutcome.TimedOut"/> when it was still live at the deadline.
    /// </returns>
    Task<SessionDrainOutcome> DrainAsync(
        SessionId sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of fencing a session's active run before a seal (issue #2903).
/// </summary>
public enum SessionDrainOutcome
{
    /// <summary>No run was bound to the session; the seal may proceed immediately.</summary>
    NoActiveRun = 0,

    /// <summary>A run was in flight, was stopped, and has settled. The seal may proceed.</summary>
    Drained = 1,

    /// <summary>
    /// A run was still in flight when the drain deadline elapsed. The caller must fail rather
    /// than seal - committing here is precisely the silent turn loss the fence exists to prevent.
    /// </summary>
    TimedOut = 2
}

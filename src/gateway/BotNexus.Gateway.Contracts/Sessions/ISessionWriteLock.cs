using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Per-session mutex that serializes the entire write→prompt→reload→save
/// window of an agent exchange. Required because the freshness gate in
/// <c>AgentExchangeCompletionGate</c> reads metadata written by a tool that
/// executes during the prompt — without this lock, two concurrent callers
/// holding the same <see cref="SessionId"/> can interleave their per-turn
/// active-exchange-id writes and satisfy each other's gate with the wrong
/// payload (issue #551, surfaced by PR #550 bug-hunt critique HIGH-1 and
/// MEDIUM-2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> Single-process only. The lock is an in-memory primitive
/// keyed by <see cref="SessionId"/>; multiple gateway instances against
/// the same persistent session store are not serialized by this contract.
/// That topology is not supported by the current store implementations
/// (no row versioning, no transactional write spanning prompts), so the
/// single-process scope matches the platform's overall consistency model.
/// </para>
/// <para>
/// <b>Non-reentrant.</b> Tools invoked inside <c>PromptAsync</c> while the
/// lock is held MUST NOT try to acquire the same session's lock — doing so
/// will deadlock. Tools operate on session metadata through
/// <see cref="ISessionStore"/> directly; the lock guards the orchestration
/// layer (relay receiver, exchange service) where the active-exchange-id
/// write→prompt→reload→consume sequence runs as a single logical unit.
/// </para>
/// <para>
/// <b>Lifetime:</b> Slots are refcounted; the dictionary entry is removed
/// as soon as the last in-flight acquire completes its release. There is
/// no per-session leak even for sessions that are sealed mid-flight.
/// </para>
/// </remarks>
public interface ISessionWriteLock
{
    /// <summary>
    /// Acquires the per-session lock for <paramref name="sessionId"/>.
    /// Blocks asynchronously if another caller holds the lock for the same
    /// session id; returns immediately if uncontested. Dispose the returned
    /// lease to release the lock (<c>await using</c> is recommended).
    /// </summary>
    /// <param name="sessionId">The session id to serialize on.</param>
    /// <param name="cancellationToken">
    /// Cancellation token that aborts the wait. The slot refcount is
    /// correctly decremented on cancellation, so a cancelled acquire does
    /// not leak a permanent dictionary entry.
    /// </param>
    /// <returns>
    /// An <see cref="IAsyncDisposable"/> lease that releases the lock when
    /// disposed.
    /// </returns>
    Task<IAsyncDisposable> AcquireAsync(SessionId sessionId, CancellationToken cancellationToken = default);
}

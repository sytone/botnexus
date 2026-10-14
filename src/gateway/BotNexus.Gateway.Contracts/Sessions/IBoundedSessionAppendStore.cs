using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Optional session-store capability for an append whose completion has a hard, store-enforced
/// deadline and whose return always represents a known commit-or-failure outcome.
/// </summary>
/// <remarks>
/// Callers may safely fail closed after this method throws: implementations must not leave work
/// running in the background or permit the append to commit after the call has returned.
/// </remarks>
public interface IBoundedSessionAppendStore
{
    /// <summary>
    /// Atomically appends entries within <paramref name="deadline"/>, returning only after commit
    /// or after the store has definitively rolled the operation back.
    /// </summary>
    /// <param name="sessionId">The existing session to append to.</param>
    /// <param name="entries">The entries to append in order.</param>
    /// <param name="deadline">Maximum duration available to acquire locks and complete the transaction.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    Task<SessionAppendMutationResult> AppendEntriesWithinAsync(
        SessionId sessionId,
        IReadOnlyList<SessionEntry> entries,
        TimeSpan deadline,
        CancellationToken cancellationToken = default);
}

/// <summary>Identifies why a bounded append could not produce a committed result.</summary>
public enum BoundedSessionAppendFailureReason
{
    /// <summary>The operation deadline expired without a commit.</summary>
    Deadline = 0,

    /// <summary>A SQLite writer lock remained held through the operation deadline.</summary>
    Locked = 1
}

/// <summary>
/// Reports a definitive bounded-append failure after the implementation has ensured that no late
/// commit can occur.
/// </summary>
public sealed class BoundedSessionAppendException : Exception
{
    /// <summary>Creates a bounded-append failure with a stable machine-readable reason.</summary>
    public BoundedSessionAppendException(
        BoundedSessionAppendFailureReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    /// <summary>Gets the classified reason the append failed.</summary>
    public BoundedSessionAppendFailureReason Reason { get; }
}

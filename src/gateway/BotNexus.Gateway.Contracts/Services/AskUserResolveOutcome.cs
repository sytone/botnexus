namespace BotNexus.Gateway.Abstractions.Services;

/// <summary>
/// Outcome of resolving an <c>ask_user</c> prompt through the durable checkpoint path
/// (issue #2047). Distinguishes the live in-memory completion from a restart-safe resume
/// reconstructed purely from persisted conversation state, and the idempotent no-op that
/// protects against duplicate or cross-client submissions.
/// </summary>
public enum AskUserResolveOutcome
{
    /// <summary>
    /// A live in-memory waiter existed and was completed directly. The original blocked
    /// tool task resumes in-process; no reconstruction was necessary.
    /// </summary>
    LiveCompleted,

    /// <summary>
    /// No live waiter existed, but a durable pending prompt matching the request id was found
    /// on the conversation row and atomically claimed. The checkpoint was cleared and a
    /// continuation was dispatched to resume execution from persisted state alone.
    /// </summary>
    ResumedFromCheckpoint,

    /// <summary>
    /// Neither a live waiter nor a durable pending prompt exists for the conversation. The
    /// submission is a no-op: an already-answered, already-cancelled, or never-issued prompt.
    /// This is the idempotent terminal that stops a duplicate or stale response from resuming
    /// the conversation a second time.
    /// </summary>
    NoPendingCheckpoint,

    /// <summary>
    /// A durable pending prompt exists but its request id does not match the supplied one -
    /// a stale request id from a client that missed a newer prompt. Left untouched.
    /// </summary>
    RequestIdMismatch
}

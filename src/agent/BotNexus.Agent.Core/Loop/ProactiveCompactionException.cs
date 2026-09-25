namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// Signals that proactive compaction was required before a provider turn but could not be applied.
/// </summary>
public sealed class ProactiveCompactionException : Exception
{
    /// <summary>
    /// Initializes a new proactive-compaction failure.
    /// </summary>
    /// <param name="message">A diagnostic message that contains no prompt content.</param>
    /// <param name="retryable">Whether retrying the run may succeed.</param>
    /// <param name="innerException">The underlying failure, when one was thrown.</param>
    public ProactiveCompactionException(
        string message,
        bool retryable,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Retryable = retryable;
    }

    /// <summary>Whether retrying the run may succeed.</summary>
    public bool Retryable { get; }
}

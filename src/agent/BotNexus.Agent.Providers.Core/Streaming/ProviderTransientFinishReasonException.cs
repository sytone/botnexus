namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// Raised when a provider terminates a stream with a <c>finish_reason</c> that names a transient
/// transport failure (#3567), so the agent loop's exception-only retry lane can classify and retry
/// it instead of ending the run on a returned message.
/// </summary>
/// <remarks>
/// The message is deliberately the same <c>Provider finish_reason: {reason}</c> string the mapping
/// used to attach to the terminal assistant message. Two consequences, both intended: the
/// loop-level <c>TransientErrorClassifier</c> matches it via the <c>network[-_\s]?error</c> transport
/// pattern with no type-specific special case, and once the attempt budget is exhausted the run
/// still ends with a diagnostic that names <c>network_error</c> - the failure stays legible rather
/// than being retried into silence.
/// </remarks>
public sealed class ProviderTransientFinishReasonException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderTransientFinishReasonException"/> class.
    /// </summary>
    /// <param name="finishReason">The raw provider finish reason that terminated the stream.</param>
    public ProviderTransientFinishReasonException(string finishReason)
        : base($"Provider finish_reason: {finishReason}")
    {
        FinishReason = finishReason;
    }

    /// <summary>The raw provider <c>finish_reason</c> that terminated the stream.</summary>
    public string FinishReason { get; }
}

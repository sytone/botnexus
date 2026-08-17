namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// Thrown to awaiters of <see cref="LlmStream.GetResultAsync"/> when a stream was terminated
/// without ever producing a final <see cref="Models.AssistantMessage"/> (#3293).
/// </summary>
/// <remarks>
/// <para>
/// Before #3293 the no-result path was expressed as <c>End(null)</c>, which completed the event
/// channel but left the result <see cref="TaskCompletionSource{TResult}"/> pending forever. Any
/// caller awaiting <see cref="LlmStream.GetResultAsync"/> - notably the agent loop's stream
/// accumulator, which awaits it whenever no terminal event was observed - was stranded with no
/// error, no cancellation and no timeout. A hung turn with nothing to diagnose is strictly worse
/// than a loud failure, so the no-result path now faults with this exception instead.
/// </para>
/// <para>
/// Producers reach this state legitimately: a transport that dies mid-parse has no message to
/// report. They signal it explicitly via <see cref="LlmStream.EndWithoutResult(string)"/>, and the
/// supplied reason is carried here so the failure names its own cause.
/// </para>
/// </remarks>
public sealed class LlmStreamIncompleteException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LlmStreamIncompleteException"/> class.
    /// </summary>
    /// <param name="reason">Producer-supplied description of why no result was produced.</param>
    public LlmStreamIncompleteException(string reason)
        : base($"LLM stream ended without a result: {reason}")
    {
        Reason = reason;
    }

    /// <summary>
    /// The producer-supplied reason the stream terminated with no final message.
    /// </summary>
    public string Reason { get; }
}

using System.Runtime.ExceptionServices;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Classifies an exception that ended a turn as caller-initiated cancellation (control flow) or a
/// genuine fault (issue #3230).
/// </summary>
/// <remarks>
/// <para>
/// Archiving a conversation, a client disconnect and host shutdown all cancel the turn's
/// <see cref="CancellationToken"/>. That is orderly teardown. The gateway's turn loop already had a
/// <c>catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)</c> branch
/// recording <c>outcome=cancelled</c>, but the cancellation raised inside the streaming pipeline
/// surfaces <b>wrapped</b> out of <c>StreamingSessionHelper.ProcessAndSaveAsync</c> (an
/// <see cref="AggregateException"/>, or an exception carrying the cancellation as its inner). A
/// type-only <c>catch</c> therefore missed it, and the turn fell through to the general handler:
/// logged <c>[ERR] Error processing message for agent</c> and recorded <c>outcome=error</c> on
/// <c>AgentExecutionDurationMs</c> for an ordinary archive.
/// </para>
/// <para>
/// This classifier walks the wrapper chain so the same cancellation is recognised whether it arrives
/// bare or nested. It deliberately answers only "is this exception cancellation-shaped"; the caller
/// must AND it with its own token state, because an <see cref="OperationCanceledException"/> raised
/// while no token was signalled is a genuine fault and must keep the Error path. Keying on token
/// state rather than exception type alone is the same distinction #3116 turned on.
/// </para>
/// </remarks>
internal static class TurnCancellationClassifier
{
    /// <summary>
    /// Maximum wrapper depth walked when unwrapping. A cancellation nested deeper than this is not
    /// worth distinguishing from a fault, and the bound makes a cyclic inner-exception chain safe.
    /// </summary>
    private const int MaxUnwrapDepth = 8;

    /// <summary>
    /// Returns true when <paramref name="exception"/> is, or wraps, an
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <remarks>
    /// This is NOT sufficient on its own to treat a turn as cancelled - the caller must also confirm
    /// its own token was signalled. See the class remarks.
    /// </remarks>
    /// <param name="exception">The exception that ended the turn.</param>
    /// <returns><see langword="true"/> if a cancellation is present anywhere in the wrapper chain.</returns>
    public static bool IsCancellation(Exception? exception)
        => IsCancellation(exception, MaxUnwrapDepth);

    private static bool IsCancellation(Exception? exception, int depthRemaining)
    {
        if (exception is null || depthRemaining <= 0)
            return false;

        if (exception is OperationCanceledException)
            return true;

        // An AggregateException can carry the cancellation alongside unrelated faults. Any
        // cancellation in the set is enough only when every fault in it is a cancellation - a
        // genuine error travelling with a cancellation is still a genuine error.
        if (exception is AggregateException aggregate)
        {
            var inners = aggregate.InnerExceptions;
            return inners.Count > 0
                && inners.All(inner => IsCancellation(inner, depthRemaining - 1));
        }

        return IsCancellation(exception.InnerException, depthRemaining - 1);
    }
}

using BotNexus.Gateway.Diagnostics;

namespace BotNexus.Gateway.Tests.Diagnostics;

/// <summary>
/// Issue #3230 AC5 - a caller-initiated cancellation that surfaces WRAPPED out of
/// <c>StreamingSessionHelper.ProcessAndSaveAsync</c> must still be recognised as cancellation, so
/// <c>GatewayHost</c> records <c>outcome=cancelled</c> rather than <c>outcome=error</c> and does not
/// log <c>[ERR] Error processing message for agent</c> for an ordinary archive.
/// </summary>
/// <remarks>
/// These tests pin the classifier in both directions. The negative cases matter most: the classifier
/// is ANDed with the host's own token state, and it must never claim a genuine fault is a
/// cancellation, or the general error path would be silently disarmed.
/// </remarks>
public sealed class TurnCancellationClassifierTests
{
    [Fact]
    public void IsCancellation_ForBareOperationCanceled_IsTrue()
        => TurnCancellationClassifier.IsCancellation(new OperationCanceledException()).ShouldBeTrue();

    [Fact]
    public void IsCancellation_ForBareTaskCanceled_IsTrue()
    {
        // TaskCanceledException derives from OperationCanceledException; this is the exact type the
        // observed ChannelWriter.WriteAsync throw produced.
        TurnCancellationClassifier.IsCancellation(new TaskCanceledException()).ShouldBeTrue();
    }

    [Fact]
    public void IsCancellation_ForCancellationWrappedInAnotherException_IsTrue()
    {
        // The reported shape: the cancellation reaches GatewayHost nested inside the exception that
        // unwound ProcessAndSaveAsync, which is why the type-only catch above it missed it.
        var wrapped = new InvalidOperationException("stream pipeline failed", new TaskCanceledException());

        TurnCancellationClassifier.IsCancellation(wrapped).ShouldBeTrue();
    }

    [Fact]
    public void IsCancellation_ForAggregateOfOnlyCancellations_IsTrue()
    {
        var aggregate = new AggregateException(new TaskCanceledException(), new OperationCanceledException());

        TurnCancellationClassifier.IsCancellation(aggregate).ShouldBeTrue();
    }

    [Fact]
    public void IsCancellation_ForAggregateMixingARealFaultWithACancellation_IsFalse()
    {
        // A genuine error travelling alongside a cancellation is still a genuine error. Returning
        // true here would let a real fault be recorded as outcome=cancelled.
        var aggregate = new AggregateException(new TaskCanceledException(), new InvalidOperationException("real"));

        TurnCancellationClassifier.IsCancellation(aggregate).ShouldBeFalse();
    }

    [Fact]
    public void IsCancellation_ForEmptyAggregate_IsFalse()
        => TurnCancellationClassifier.IsCancellation(new AggregateException()).ShouldBeFalse();

    [Fact]
    public void IsCancellation_ForOrdinaryFault_IsFalse()
        => TurnCancellationClassifier.IsCancellation(new InvalidOperationException("boom")).ShouldBeFalse();

    [Fact]
    public void IsCancellation_ForDeeplyNestedFaultWithNoCancellation_IsFalse()
    {
        var nested = new InvalidOperationException("a", new ApplicationException("b", new FormatException("c")));

        TurnCancellationClassifier.IsCancellation(nested).ShouldBeFalse();
    }

    [Fact]
    public void IsCancellation_ForNull_IsFalse()
        => TurnCancellationClassifier.IsCancellation(null).ShouldBeFalse();

    [Fact]
    public void IsCancellation_ForCancellationNestedBeyondTheUnwrapBound_IsFalse()
    {
        // The walk is depth-bounded so a pathological or cyclic chain cannot hang the error path.
        // Beyond the bound the classifier fails CLOSED - toward "fault" - which is the safe
        // direction: the turn is still reported, just as an error rather than a cancellation.
        Exception current = new TaskCanceledException();
        for (var i = 0; i < 12; i++)
            current = new InvalidOperationException($"layer-{i}", current);

        TurnCancellationClassifier.IsCancellation(current).ShouldBeFalse();
    }
}

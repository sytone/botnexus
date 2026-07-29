using System.Net;
using BotNexus.Gateway.Channels.Startup;

namespace BotNexus.Gateway.Channels.Tests;

/// <summary>
/// #2386 - the steady-state polling loops retried every failure immediately and forever.
/// These tests pin the bounded behaviour: transient faults back off exponentially to a cap,
/// terminal faults trip the breaker and stop the loop, and anything unrecognised fails closed.
/// </summary>
public sealed class ChannelLoopCircuitBreakerTests
{
    private static ChannelLoopCircuitBreaker CreateBreaker()
        => new("telegram bot 'farnsworth' polling loop");

    [Fact]
    public void TransientFailures_BackOffExponentiallyAndCapAt30Seconds()
    {
        var breaker = CreateBreaker();
        var transient = new HttpRequestException("upstream", null, HttpStatusCode.BadGateway);

        var delays = new List<TimeSpan>();
        for (var i = 0; i < 8; i++)
        {
            var response = breaker.RecordFailure(transient);
            response.Kind.ShouldBe(ChannelFailureKind.Transient);
            response.ShouldStop.ShouldBeFalse();
            delays.Add(response.RetryDelay);
        }

        // base 2s, doubling, capped at 30s.
        delays[0].ShouldBe(TimeSpan.FromSeconds(2));
        delays[1].ShouldBe(TimeSpan.FromSeconds(4));
        delays[2].ShouldBe(TimeSpan.FromSeconds(8));
        delays[3].ShouldBe(TimeSpan.FromSeconds(16));
        delays[4].ShouldBe(TimeSpan.FromSeconds(30));
        delays[5].ShouldBe(TimeSpan.FromSeconds(30));
        delays[6].ShouldBe(TimeSpan.FromSeconds(30));
        delays[7].ShouldBe(TimeSpan.FromSeconds(30));

        breaker.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public void TransientFailures_NeverOverflowIntoNegativeOrZeroDelays()
    {
        var breaker = CreateBreaker();
        var transient = new HttpRequestException("upstream", null, HttpStatusCode.ServiceUnavailable);

        // Far beyond any sane shift width: the exponent must be clamped before shifting.
        for (var i = 0; i < 200; i++)
        {
            var response = breaker.RecordFailure(transient);
            response.RetryDelay.ShouldBe(
                i == 0 ? TimeSpan.FromSeconds(2) :
                i == 1 ? TimeSpan.FromSeconds(4) :
                i == 2 ? TimeSpan.FromSeconds(8) :
                i == 3 ? TimeSpan.FromSeconds(16) : TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public void Success_ResetsTheBackoffSchedule()
    {
        var breaker = CreateBreaker();
        var transient = new TimeoutException();

        breaker.RecordFailure(transient).RetryDelay.ShouldBe(TimeSpan.FromSeconds(2));
        breaker.RecordFailure(transient).RetryDelay.ShouldBe(TimeSpan.FromSeconds(4));

        breaker.RecordSuccess();

        breaker.ConsecutiveTransientFailures.ShouldBe(0);
        breaker.RecordFailure(transient).RetryDelay.ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void TerminalFailure_TripsTheBreakerAndStopsTheLoop()
    {
        var breaker = CreateBreaker();

        // The observed Telegram incident: HTTP 409 "terminated by other getUpdates request".
        var conflict = new HttpRequestException("terminated by other getUpdates request", null, HttpStatusCode.Conflict);

        var response = breaker.RecordFailure(conflict);

        response.Kind.ShouldBe(ChannelFailureKind.Terminal);
        response.ShouldStop.ShouldBeTrue();
        response.CircuitOpened.ShouldBeTrue();
        response.RetryDelay.ShouldBe(TimeSpan.Zero);
        breaker.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public void TerminalFailure_OpensTheCircuitOnlyOnceSoTheErrorIsLoggedOnce()
    {
        var breaker = CreateBreaker();
        var terminal = new HttpRequestException("revoked", null, HttpStatusCode.Unauthorized);

        breaker.RecordFailure(terminal).CircuitOpened.ShouldBeTrue();
        breaker.RecordFailure(terminal).CircuitOpened.ShouldBeFalse();
        breaker.RecordFailure(terminal).CircuitOpened.ShouldBeFalse();

        breaker.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public void UnrecognisedFailure_FailsClosedAndStopsTheLoop()
    {
        var breaker = CreateBreaker();

        var response = breaker.RecordFailure(new NotSupportedException("something nobody classified"));

        response.Kind.ShouldBe(ChannelFailureKind.Terminal);
        response.ShouldStop.ShouldBeTrue();
        breaker.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public void RecordFailure_NullException_Throws()
    {
        var breaker = CreateBreaker();

        Should.Throw<ArgumentNullException>(() => breaker.RecordFailure(null!));
    }

    [Fact]
    public void LoopDescription_IsSurfacedForTheSingleDegradedErrorLine()
        => CreateBreaker().LoopDescription.ShouldBe("telegram bot 'farnsworth' polling loop");
}

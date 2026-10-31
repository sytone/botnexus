using BotNexus.Testing;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Pins the shared test coordination contract so individual projects do not invent polling semantics.
/// </summary>
public class TestAwaitBehaviourTests
{
    /// <summary>Conditions that are already true complete without scheduling a delay.</summary>
    [Fact]
    public async Task EventuallyAsync_ConditionAlreadyTrue_DoesNotDelay()
    {
        var delayCalls = 0;

        await TestAwait.EventuallyAsync(
            () => true,
            "condition to become true",
            timeout: TimeSpan.FromSeconds(30),
            delayAsync: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            });

        delayCalls.ShouldBe(0);
    }

    /// <summary>Asynchronous conditions are retried through the same implementation as synchronous ones.</summary>
    [Fact]
    public async Task EventuallyAsync_AsyncConditionBecomesTrue_RetriesUntilObserved()
    {
        var attempts = 0;

        await TestAwait.EventuallyAsync(
            () => Task.FromResult(++attempts == 3),
            "third observation",
            timeout: TimeSpan.FromSeconds(30),
            delayAsync: (_, _) => Task.CompletedTask);

        attempts.ShouldBe(3);
    }

    /// <summary>Caller cancellation remains distinguishable from an unmet-condition timeout.</summary>
    [Fact]
    public async Task EventuallyAsync_CallerCancels_ThrowsOperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Func<Task> act = () => TestAwait.EventuallyAsync(
            () => false,
            "condition to become true",
            cancellationToken: cancellation.Token);

        await Should.ThrowAsync<OperationCanceledException>(act);
    }

    /// <summary>Timeout diagnostics identify the observation that never occurred.</summary>
    [Fact]
    public async Task EventuallyAsync_ConditionNeverTrue_ReportsDescriptionAndAttempts()
    {
        Func<Task> act = () => TestAwait.EventuallyAsync(
            () => false,
            "completion event to be recorded",
            timeout: TimeSpan.Zero,
            delayAsync: (_, _) => Task.CompletedTask);

        var exception = await Should.ThrowAsync<TimeoutException>(act);
        exception.Message.ShouldContain("completion event to be recorded");
        exception.Message.ShouldContain("1 attempt");
    }

    /// <summary>A deadline crossing cannot turn a bounded poll into a negative or infinite delay.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EventuallyAsync_DeadlineCrossesBetweenClockReads_KeepsDelayPositive(int overshootMilliseconds)
    {
        // The original two-read calculation sees 49ms for its check, then 51/52ms for
        // subtraction. In particular, -1ms is Task.Delay's infinite-delay sentinel.
        var clock = new ScriptedTimestampProvider(0, 49, 50 + overshootMilliseconds);
        var delays = new List<TimeSpan>();
        var attempts = 0;
        Func<Task> act = () => TestAwait.EventuallyAsync(
            () => { attempts++; return false; },
            "deadline crossing observation",
            timeout: TimeSpan.FromMilliseconds(50),
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                delay.ShouldBeGreaterThan(TimeSpan.Zero);
                delay.ShouldBeLessThanOrEqualTo(TimeSpan.FromMilliseconds(1));
                return Task.CompletedTask;
            },
            timeProvider: clock);

        var exception = await Should.ThrowAsync<TimeoutException>(act);
        exception.Message.ShouldContain("deadline crossing observation");
        exception.Message.ShouldContain("2 attempts");
        attempts.ShouldBe(2);
        delays.ShouldBe(new[] { TimeSpan.FromMilliseconds(1) });
        clock.TimestampReads.ShouldBe(3);
    }

    /// <summary>At or past the deadline the last failed observation times out without a delay.</summary>
    [Theory]
    [InlineData(50)]
    [InlineData(51)]
    public async Task EventuallyAsync_DeadlineReached_DoesNotDelay(int elapsedMilliseconds)
    {
        var clock = new ScriptedTimestampProvider(0, elapsedMilliseconds);
        var delays = 0;
        Func<Task> act = () => TestAwait.EventuallyAsync(
            () => false,
            "deadline reached",
            timeout: TimeSpan.FromMilliseconds(50),
            delayAsync: (_, _) => { delays++; return Task.CompletedTask; },
            timeProvider: clock);

        var exception = await Should.ThrowAsync<TimeoutException>(act);
        exception.Message.ShouldContain("1 attempt");
        delays.ShouldBe(0);
        clock.TimestampReads.ShouldBe(2);
    }

    /// <summary>Polling is capped by the interval when more of the observation window remains.</summary>
    [Fact]
    public async Task EventuallyAsync_PollIntervalShorterThanRemaining_UsesPollInterval()
    {
        var clock = new ScriptedTimestampProvider(0, 20, 50);
        var delays = new List<TimeSpan>();
        Func<Task> act = () => TestAwait.EventuallyAsync(
            () => Task.FromResult(false),
            "bounded polling",
            timeout: TimeSpan.FromMilliseconds(50),
            pollInterval: TimeSpan.FromMilliseconds(10),
            delayAsync: (delay, _) => { delays.Add(delay); return Task.CompletedTask; },
            timeProvider: clock);

        await Should.ThrowAsync<TimeoutException>(act);
        delays.ShouldBe(new[] { TimeSpan.FromMilliseconds(10) });
    }

    /// <summary>A successful asynchronous observation still wins even after the clock deadline.</summary>
    [Fact]
    public async Task EventuallyAsync_AsyncConditionTrueAfterDeadline_Completes()
    {
        var clock = new ScriptedTimestampProvider(0, 49, 51);
        var attempts = 0;
        var delays = new List<TimeSpan>();

        await TestAwait.EventuallyAsync(
            () =>
            {
                attempts++;
                if (attempts == 2)
                    clock.GetTimestamp().ShouldBe(51);
                return Task.FromResult(attempts == 2);
            },
            "successful final observation",
            timeout: TimeSpan.FromMilliseconds(50),
            delayAsync: (delay, _) => { delays.Add(delay); return Task.CompletedTask; },
            timeProvider: clock);

        attempts.ShouldBe(2);
        delays.ShouldBe(new[] { TimeSpan.FromMilliseconds(1) });
        clock.TimestampReads.ShouldBe(3);
    }

    /// <summary>The injected delay receives the caller token and preserves cancellation identity.</summary>
    [Fact]
    public async Task EventuallyAsync_CanceledDuringDelay_PreservesCallerToken()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new ScriptedTimestampProvider(0, 49, 51);
        var attempts = 0;
        var delays = 0;
        Func<Task> act = () => TestAwait.EventuallyAsync(
            () => { attempts++; return false; },
            "canceled polling",
            timeout: TimeSpan.FromMilliseconds(50),
            cancellationToken: cancellation.Token,
            delayAsync: (delay, token) =>
            {
                delays++;
                delay.ShouldBe(TimeSpan.FromMilliseconds(1));
                token.ShouldBe(cancellation.Token);
                cancellation.Cancel();
                return Task.FromCanceled(token);
            },
            timeProvider: clock);

        var exception = await Should.ThrowAsync<OperationCanceledException>(act);
        exception.CancellationToken.ShouldBe(cancellation.Token);
        attempts.ShouldBe(1);
        delays.ShouldBe(1);
    }

    // Scripted monotonic reads force the exact arithmetic interleaving without sleeps or timers.
    private sealed class ScriptedTimestampProvider(params long[] timestamps) : TimeProvider
    {
        public int TimestampReads { get; private set; }

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp()
        {
            var index = Math.Min(TimestampReads, timestamps.Length - 1);
            TimestampReads++;
            return timestamps[index];
        }

        public override DateTimeOffset GetUtcNow() =>
            throw new InvalidOperationException("Polling deadlines must use monotonic timestamps.");
    }

    /// <summary>Elapsed-time tests can advance a shared clock without waiting for wall time.</summary>
    [Fact]
    public void ManualTimeProvider_Advance_MovesUtcNow()
    {
        var start = DateTimeOffset.Parse("2026-08-21T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var timeProvider = new ManualTimeProvider(start);

        timeProvider.Advance(TimeSpan.FromMinutes(5));

        timeProvider.GetUtcNow().ShouldBe(start.AddMinutes(5));
    }
}
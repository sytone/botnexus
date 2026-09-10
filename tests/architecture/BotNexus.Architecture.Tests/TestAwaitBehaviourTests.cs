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

    /// <summary>A signal already raised is observed without spending any of the deadline.</summary>
    [Fact]
    public async Task SignaledAsync_SignalAlreadyRaised_ReturnsImmediately()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();

        await TestAwait.SignaledAsync(signal.Task, "the fixture to report readiness");
    }

    /// <summary>The value a signal carries is handed back to the caller.</summary>
    [Fact]
    public async Task SignaledAsync_SignalCarriesValue_ReturnsIt()
    {
        var signal = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult(7);

        (await TestAwait.SignaledAsync(signal.Task, "the fixture to report a value")).ShouldBe(7);
    }

    /// <summary>A failure in the awaited work surfaces as itself, not as a timeout.</summary>
    [Fact]
    public async Task SignaledAsync_SignalFaults_PropagatesTheOriginalException()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetException(new InvalidOperationException("the fixture blew up"));

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => TestAwait.SignaledAsync(signal.Task, "the fixture to report readiness"));
        exception.Message.ShouldBe("the fixture blew up");
    }

    /// <summary>
    /// An unraised signal is reported by name, so the failure says what never happened rather than
    /// printing a bare <see cref="TimeoutException"/>.
    /// </summary>
    [Fact]
    public async Task SignaledAsync_SignalNeverRaised_NamesTheSignal()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var exception = await Should.ThrowAsync<TimeoutException>(
            // deadline-is-the-assertion: expiry is the passing outcome, so this must NOT be generous.
            () => TestAwait.SignaledAsync(never.Task, "the boundary log to be written", timeout: TimeSpan.Zero));

        exception.Message.ShouldContain("the boundary log to be written");
    }

    /// <summary>
    /// A <see cref="TimeoutException"/> raised by the awaited work itself is NOT rewritten as
    /// "the signal was never raised" — production types derive from it and carry precise detail.
    /// </summary>
    [Fact]
    public async Task SignaledAsync_SignalFaultsWithATimeout_DoesNotReplaceIt()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetException(new TimeoutException("the stripe lock bound elapsed for key 'k'"));

        var exception = await Should.ThrowAsync<TimeoutException>(
            () => TestAwait.SignaledAsync(signal.Task, "the lock to be handed over"));

        exception.Message.ShouldBe("the stripe lock bound elapsed for key 'k'");
    }

    /// <summary>Caller cancellation remains distinguishable from an unraised signal.</summary>
    [Fact]
    public async Task SignaledAsync_CallerCancels_ThrowsOperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Should.ThrowAsync<OperationCanceledException>(
            () => TestAwait.SignaledAsync(
                never.Task,
                "the boundary log to be written",
                cancellationToken: cancellation.Token));
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
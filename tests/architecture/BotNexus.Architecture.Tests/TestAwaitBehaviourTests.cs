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
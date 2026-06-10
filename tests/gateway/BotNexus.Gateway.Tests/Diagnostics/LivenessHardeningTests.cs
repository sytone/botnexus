using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BotNexus.Gateway.Tests.Diagnostics;

public sealed class ThreadPoolWatchdogServiceTests
{
    private static IOptions<ThreadPoolWatchdogOptions> DefaultOptions(Action<ThreadPoolWatchdogOptions>? configure = null)
    {
        var options = new ThreadPoolWatchdogOptions();
        configure?.Invoke(options);
        return Options.Create(options);
    }

    [Fact]
    public void CheckThreadPool_BelowThreshold_NoWarning()
    {
        // Arrange
        var logger = new FakeLogger<ThreadPoolWatchdogService>();
        var metrics = new FakeThreadPoolMetrics { PendingWorkItemCount = 5 };
        var service = new ThreadPoolWatchdogService(DefaultOptions(), logger, metrics);

        // Act
        service.CheckThreadPool();

        // Assert
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void CheckThreadPool_AboveThreshold_EmitsWarning()
    {
        // Arrange
        var logger = new FakeLogger<ThreadPoolWatchdogService>();
        var metrics = new FakeThreadPoolMetrics { PendingWorkItemCount = 200 };
        var service = new ThreadPoolWatchdogService(DefaultOptions(), logger, metrics);

        // Act
        service.CheckThreadPool();

        // Assert
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void CheckThreadPool_RepeatedAboveThreshold_OnlyEmitsOnce()
    {
        // Arrange
        var logger = new FakeLogger<ThreadPoolWatchdogService>();
        var metrics = new FakeThreadPoolMetrics { PendingWorkItemCount = 200 };
        var service = new ThreadPoolWatchdogService(DefaultOptions(), logger, metrics);

        // Act
        service.CheckThreadPool();
        service.CheckThreadPool();
        service.CheckThreadPool();

        // Assert: only one warning, not three
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void CheckThreadPool_RecoveryThenExceedAgain_EmitsSecondWarning()
    {
        // Arrange
        var logger = new FakeLogger<ThreadPoolWatchdogService>();
        var metrics = new FakeThreadPoolMetrics { PendingWorkItemCount = 200 };
        var service = new ThreadPoolWatchdogService(DefaultOptions(), logger, metrics);

        // Act: exceed threshold
        service.CheckThreadPool();
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);

        // Recover below threshold
        metrics.PendingWorkItemCount = 5;
        service.CheckThreadPool();

        // Exceed again
        metrics.PendingWorkItemCount = 200;
        service.CheckThreadPool();

        // Assert: two warnings total (one per exceedance cycle)
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    [Fact]
    public void CheckThreadPool_WarningIncludesThreadCounts()
    {
        // Arrange
        var logger = new FakeLogger<ThreadPoolWatchdogService>();
        var metrics = new FakeThreadPoolMetrics { PendingWorkItemCount = 200 };
        var service = new ThreadPoolWatchdogService(DefaultOptions(), logger, metrics);

        // Act
        service.CheckThreadPool();

        // Assert: message contains threadpool diagnostic info
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("200", warning.Message);
        Assert.Contains("100", warning.Message); // threshold
    }
}

public sealed class HealthEndpointTimeoutTests
{
    [Fact]
    public async Task HealthCheck_WhenNotTimedOut_ReturnsStatus()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await HealthEndpointHelper.ExecuteWithTimeoutAsync(
            () => Task.FromResult(new HealthResponse("ok", null, null)),
            cts.Token);

        Assert.Equal("ok", result.Status);
    }

    [Fact]
    public async Task HealthCheck_WhenTimedOut_ReturnsTimeoutStatus()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await HealthEndpointHelper.ExecuteWithTimeoutAsync(
            () => Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => new HealthResponse("ok", null, null)),
            cts.Token);

        Assert.Equal("timeout", result.Status);
    }

    [Fact]
    public async Task HealthCheck_WhenTimedOut_ReturnsNullFields()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await HealthEndpointHelper.ExecuteWithTimeoutAsync(
            () => Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => new HealthResponse("ok", "2026-01-01", 42.0)),
            cts.Token);

        Assert.Null(result.LastActivity);
        Assert.Null(result.InactivitySeconds);
    }

    [Fact]
    public async Task HealthCheck_PreservesResponseFields()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await HealthEndpointHelper.ExecuteWithTimeoutAsync(
            () => Task.FromResult(new HealthResponse("degraded", "2026-06-09T12:00:00Z", 600.5)),
            cts.Token);

        Assert.Equal("degraded", result.Status);
        Assert.Equal("2026-06-09T12:00:00Z", result.LastActivity);
        Assert.Equal(600.5, result.InactivitySeconds);
    }
}

public sealed class LockTimeoutLoggerTests
{
    [Fact]
    public async Task AcquireAsync_WhenFast_NoWarning()
    {
        // Arrange
        var logger = new FakeLogger<LockTimeoutLogger>();
        var semaphore = new SemaphoreSlim(1, 1);
        var lockLogger = new LockTimeoutLogger(logger, TimeSpan.FromSeconds(5));

        // Act
        using (await lockLogger.AcquireAsync(semaphore, "TestLock", CancellationToken.None))
        {
            // hold briefly
        }

        // Assert: no warning logged
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task AcquireAsync_WhenSlow_EmitsWarning()
    {
        // Arrange
        var logger = new FakeLogger<LockTimeoutLogger>();
        var semaphore = new SemaphoreSlim(1, 1);
        var lockLogger = new LockTimeoutLogger(logger, TimeSpan.FromMilliseconds(50));

        // Hold the semaphore to force contention
        await semaphore.WaitAsync();

        // Act: try to acquire with very short threshold — will log warning
        var acquireTask = lockLogger.AcquireAsync(semaphore, "TestLock", CancellationToken.None);

        // Wait for the warning threshold to fire
        await Task.Delay(200);

        // Release so the acquire completes
        semaphore.Release();
        using (await acquireTask)
        {
            // acquired after delay
        }

        // Assert: warning was logged about slow acquisition
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("TestLock"));
    }

    [Fact]
    public async Task AcquireAsync_ReleasesOnDispose()
    {
        // Arrange
        var logger = new FakeLogger<LockTimeoutLogger>();
        var semaphore = new SemaphoreSlim(1, 1);
        var lockLogger = new LockTimeoutLogger(logger, TimeSpan.FromSeconds(5));

        // Act: acquire and dispose
        using (await lockLogger.AcquireAsync(semaphore, "TestLock", CancellationToken.None))
        {
            Assert.Equal(0, semaphore.CurrentCount);
        }

        // Assert: semaphore released after dispose
        Assert.Equal(1, semaphore.CurrentCount);
    }
}

/// <summary>Fake threadpool metrics for deterministic testing.</summary>
internal sealed class FakeThreadPoolMetrics : IThreadPoolMetrics
{
    public long PendingWorkItemCount { get; set; }

    public (int WorkerAvailable, int WorkerMax, int WorkerMin, int IoAvailable, int IoMax, int IoMin) GetThreadCounts()
        => (8, 16, 4, 8, 16, 4);
}

/// <summary>Simple in-memory logger for testing.</summary>
public sealed class FakeLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }

    public record LogEntry(LogLevel Level, string Message);
}

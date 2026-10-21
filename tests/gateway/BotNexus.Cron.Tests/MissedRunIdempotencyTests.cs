using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Regression coverage for #2477: repeated startup scans must not accumulate duplicate
/// <c>missed</c> rows for the same scheduled occurrence, the recorded row must carry the
/// scheduled occurrence instant (not the scan wall-clock), and a missed-run scan must not
/// masquerade as a real execution by clobbering the job's last-run bookkeeping.
/// </summary>
public sealed class MissedRunIdempotencyTests
{
    [Fact]
    public async Task StartAsync_RunTwice_DoesNotDuplicateMissedRuns()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 6, 11, 12, 4, 59, TimeSpan.Zero));
        var job = CronStoreTestContext.CreateJob("dup-job") with
        {
            Schedule = "*/5 * * * *",
            TimeZone = "UTC",
            LastRunAt = clock.GetUtcNow().AddMinutes(-32)
        };
        await context.Store.CreateAsync(job);

        var service = new MissedRunDetectionService(
            context.Store, null!, NullLogger<MissedRunDetectionService>.Instance, clock);

        await service.StartAsync(CancellationToken.None);
        var afterFirst = (await context.Store.GetRunHistoryAsync(JobId.From("dup-job"), limit: 500))
            .Where(r => r.Status == MissedRunDetectionService.MissedStatus)
            .ToList();

        // The injected clock keeps both startups on exactly the same scan window.
        await service.StartAsync(CancellationToken.None);
        var afterSecond = (await context.Store.GetRunHistoryAsync(JobId.From("dup-job"), limit: 500))
            .Where(r => r.Status == MissedRunDetectionService.MissedStatus)
            .ToList();

        afterFirst.Count.ShouldBeGreaterThan(0);
        afterSecond.Count.ShouldBe(afterFirst.Count);
        afterSecond.Select(r => r.StartedAt).Distinct().Count().ShouldBe(afterSecond.Count);
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(1, 6)]
    [InlineData(2, 7)]
    public async Task StartAsync_AdvancingScanWindow_RecordsOnlyNewOccurrences(int advanceSeconds, int expectedCount)
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var scanTime = new DateTimeOffset(2026, 6, 11, 12, 4, 59, TimeSpan.Zero);
        var clock = new ManualTimeProvider(scanTime);
        var job = CronStoreTestContext.CreateJob("boundary-job") with
        {
            Schedule = "*/5 * * * *",
            TimeZone = "UTC",
            LastRunAt = scanTime.AddMinutes(-32)
        };
        await context.Store.CreateAsync(job);
        var service = new MissedRunDetectionService(
            context.Store, null!, NullLogger<MissedRunDetectionService>.Instance, clock);

        await service.StartAsync(CancellationToken.None);
        var original = (await context.Store.GetRunHistoryAsync(job.Id, limit: 500))
            .Where(r => r.Status == MissedRunDetectionService.MissedStatus).ToList();
        original.Count.ShouldBe(6);

        // The scan ceiling is exclusive: 12:05 is missed only after that instant.
        clock.Advance(TimeSpan.FromSeconds(advanceSeconds));
        await service.StartAsync(CancellationToken.None);
        var advanced = (await context.Store.GetRunHistoryAsync(job.Id, limit: 500))
            .Where(r => r.Status == MissedRunDetectionService.MissedStatus).ToList();
        advanced.Count.ShouldBe(expectedCount);
        advanced.Select(r => r.StartedAt).Distinct().Count().ShouldBe(expectedCount);
        foreach (var run in original)
        {
            advanced.ShouldContain(r => r.Id == run.Id && r.StartedAt == run.StartedAt);
        }
        advanced.Select(r => r.StartedAt.ToUniversalTime()).OrderBy(t => t)
            .ShouldBe(Enumerable.Range(0, expectedCount)
                .Select(i => new DateTimeOffset(2026, 6, 11, 11, 35, 0, TimeSpan.Zero).AddMinutes(i * 5)));

        await service.StartAsync(CancellationToken.None);
        var replayed = (await context.Store.GetRunHistoryAsync(job.Id, limit: 500))
            .Where(r => r.Status == MissedRunDetectionService.MissedStatus).ToList();
        replayed.Count.ShouldBe(expectedCount);
        replayed.Select(r => r.Id).OrderBy(id => id.Value)
            .ShouldBe(advanced.Select(r => r.Id).OrderBy(id => id.Value));
    }

    [Fact]
    public async Task StartAsync_StampsScheduledOccurrenceAsStartedAt()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        var job = CronStoreTestContext.CreateJob("occurrence-job") with
        {
            Schedule = "*/5 * * * *",
            LastRunAt = DateTimeOffset.UtcNow.AddMinutes(-32)
        };
        await context.Store.CreateAsync(job);

        var service = new MissedRunDetectionService(
            context.Store, null!, NullLogger<MissedRunDetectionService>.Instance);
        await service.StartAsync(CancellationToken.None);

        var recorded = (await context.Store.GetRunHistoryAsync(JobId.From("occurrence-job"), limit: 500))
            .Where(r => r.Status == MissedRunDetectionService.MissedStatus)
            .Select(r => r.StartedAt.ToUniversalTime())
            .ToList();

        // Every recorded missed row must sit exactly on a */5 cron boundary, which only
        // holds when the scheduled occurrence - not the scan wall-clock - is persisted.
        recorded.Count.ShouldBeGreaterThan(0);
        foreach (var stamp in recorded)
        {
            stamp.Second.ShouldBe(0);
            stamp.Millisecond.ShouldBe(0);
            (stamp.Minute % 5).ShouldBe(0);
        }
    }

    [Fact]
    public async Task StartAsync_DoesNotClobberLastRunBookkeeping()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        var lastRun = DateTimeOffset.UtcNow.AddMinutes(-32);
        var job = CronStoreTestContext.CreateJob("bookkeeping-job") with
        {
            Schedule = "*/5 * * * *",
            LastRunAt = lastRun,
            LastRunStatus = "ok"
        };
        await context.Store.CreateAsync(job);

        var service = new MissedRunDetectionService(
            context.Store, null!, NullLogger<MissedRunDetectionService>.Instance);
        await service.StartAsync(CancellationToken.None);

        var reloaded = await context.Store.GetAsync(JobId.From("bookkeeping-job"));
        reloaded.ShouldNotBeNull();
        reloaded.LastRunStatus.ShouldBe("ok");
        reloaded.LastRunAt!.Value.ToUniversalTime()
            .ShouldBe(lastRun.ToUniversalTime(), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TryRecordMissedRunAsync_SameOccurrenceTwice_InsertsOnce()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        var job = CronStoreTestContext.CreateJob("store-idem-job");
        await context.Store.CreateAsync(job);

        var occurrence = new DateTimeOffset(2026, 6, 11, 12, 5, 0, TimeSpan.Zero);

        var first = await context.Store.TryRecordMissedRunAsync(job.Id, occurrence);
        var second = await context.Store.TryRecordMissedRunAsync(job.Id, occurrence);

        first.ShouldBeTrue();
        second.ShouldBeFalse();

        var missed = (await context.Store.GetRunHistoryAsync(job.Id, limit: 500))
            .Where(r => r.Status == MissedRunDetectionService.MissedStatus)
            .ToList();
        missed.Count.ShouldBe(1);
        missed[0].StartedAt.ToUniversalTime().ShouldBe(occurrence);
        missed[0].CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task StartAsync_WhenCapTruncates_LogsDiagnostic()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        var job = CronStoreTestContext.CreateJob("cap-job") with
        {
            Schedule = "* * * * *",
            LastRunAt = DateTimeOffset.UtcNow.AddMinutes(-300)
        };
        await context.Store.CreateAsync(job);

        var logger = new CapturingLogger<MissedRunDetectionService>();
        var service = new MissedRunDetectionService(context.Store, null!, logger);

        await service.StartAsync(CancellationToken.None);

        logger.Messages.ShouldContain(m => m.Contains("truncat", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Messages.Add(formatter(state, exception));
        }
    }
}

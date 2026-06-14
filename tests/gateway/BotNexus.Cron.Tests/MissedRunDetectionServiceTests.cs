using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging.Abstractions;


namespace BotNexus.Cron.Tests;

public sealed class MissedRunDetectionServiceTests
{
    [Fact]
    public void GetMissedRuns_NoLastRun_ReturnsEmpty()
    {
        var job = CreateJob("j1") with { LastRunAt = null };

        var result = MissedRunDetectionService.GetMissedRuns(job, DateTimeOffset.UtcNow);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void GetMissedRuns_NoMissedRuns_ReturnsEmpty()
    {
        // Last ran 30 seconds ago, schedule is every 5 minutes — next run hasn't been missed yet.
        // Use a fixed time whose preceding 30-second window contains no 5-minute boundary
        // (:00/:05/:10/...). Using DateTimeOffset.UtcNow here is flaky: when the live clock lands
        // on a 5-minute boundary within the first 30 seconds, a scheduled occurrence falls inside
        // (now-30s, now) and is reported as missed, failing the assertion ~6% of the time.
        var now = new DateTimeOffset(2026, 6, 11, 12, 2, 30, TimeSpan.Zero);
        var job = CreateJob("j1") with
        {
            Schedule = "*/5 * * * *",
            LastRunAt = now.AddSeconds(-30) // 12:02:00 — no */5 boundary in (12:02:00, 12:02:30)
        };

        var result = MissedRunDetectionService.GetMissedRuns(job, now);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void GetMissedRuns_SingleMissedRun_ReturnsOne()
    {
        // Schedule every 5 minutes, last ran 10 minutes ago — should have one missed run
        var now = new DateTimeOffset(2026, 6, 11, 12, 10, 0, TimeSpan.Zero);
        var job = CreateJob("j1") with
        {
            Schedule = "*/5 * * * *",
            LastRunAt = now.AddMinutes(-10) // 12:00
        };

        var result = MissedRunDetectionService.GetMissedRuns(job, now);

        result.Count.ShouldBe(1);
        result[0].ShouldBe(new DateTimeOffset(2026, 6, 11, 12, 5, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetMissedRuns_MultipleMissedRuns_ReturnsAll()
    {
        // Schedule every 5 minutes, last ran 20 minutes ago — should have 3 missed runs (at :05, :10, :15 — but not :20 which is "now")
        var now = new DateTimeOffset(2026, 6, 11, 12, 20, 0, TimeSpan.Zero);
        var job = CreateJob("j1") with
        {
            Schedule = "*/5 * * * *",
            LastRunAt = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero)
        };

        var result = MissedRunDetectionService.GetMissedRuns(job, now);

        result.Count.ShouldBe(3);
        result[0].ShouldBe(new DateTimeOffset(2026, 6, 11, 12, 5, 0, TimeSpan.Zero));
        result[1].ShouldBe(new DateTimeOffset(2026, 6, 11, 12, 10, 0, TimeSpan.Zero));
        result[2].ShouldBe(new DateTimeOffset(2026, 6, 11, 12, 15, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetMissedRuns_InvalidSchedule_ReturnsEmpty()
    {
        var job = CreateJob("j1") with
        {
            Schedule = "not-a-cron-expression",
            LastRunAt = DateTimeOffset.UtcNow.AddHours(-1)
        };

        var result = MissedRunDetectionService.GetMissedRuns(job, DateTimeOffset.UtcNow);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void GetMissedRuns_CapsAtMaximum()
    {
        // Schedule every minute, last ran 200 minutes ago — capped at 100
        var now = new DateTimeOffset(2026, 6, 11, 15, 0, 0, TimeSpan.Zero);
        var job = CreateJob("j1") with
        {
            Schedule = "* * * * *",
            LastRunAt = now.AddMinutes(-200)
        };

        var result = MissedRunDetectionService.GetMissedRuns(job, now);

        result.Count.ShouldBe(100);
    }

    [Fact]
    public async Task StartAsync_RecordsMissedRuns()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;

        // Create a job that last ran 10 minutes ago with 5-minute schedule
        var job = CronStoreTestContext.CreateJob("missed-job") with
        {
            Schedule = "*/5 * * * *",
            LastRunAt = now.AddMinutes(-12)
        };
        await context.Store.CreateAsync(job);

        var service = new MissedRunDetectionService(
            context.Store, null!, NullLogger<MissedRunDetectionService>.Instance);

        await service.StartAsync(CancellationToken.None);

        var history = await context.Store.GetRunHistoryAsync(JobId.From("missed-job"), limit: 50);
        history.ShouldContain(r => r.Status == MissedRunDetectionService.MissedStatus);
    }

    [Fact]
    public async Task StartAsync_SkipsDisabledJobs()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;

        var job = CronStoreTestContext.CreateJob("disabled-job", enabled: false) with
        {
            Schedule = "*/5 * * * *",
            LastRunAt = now.AddMinutes(-30)
        };
        await context.Store.CreateAsync(job);

        var service = new MissedRunDetectionService(
            context.Store, null!, NullLogger<MissedRunDetectionService>.Instance);

        await service.StartAsync(CancellationToken.None);

        var history = await context.Store.GetRunHistoryAsync(JobId.From("disabled-job"), limit: 50);
        history.ShouldBeEmpty();
    }

    [Fact]
    public void GetMissedRuns_WithTimezone_CalculatesCorrectly()
    {
        // Job in America/Los_Angeles, schedule at top of hour
        var now = new DateTimeOffset(2026, 6, 11, 20, 30, 0, TimeSpan.Zero); // 1:30 PM PDT
        var job = CreateJob("tz-job") with
        {
            Schedule = "0 * * * *", // every hour
            LastRunAt = new DateTimeOffset(2026, 6, 11, 18, 0, 0, TimeSpan.Zero), // 11:00 AM PDT
            TimeZone = "America/Los_Angeles"
        };

        var result = MissedRunDetectionService.GetMissedRuns(job, now);

        // Should have missed :00 at 19:00 UTC (12:00 PM PDT) and 20:00 UTC (1:00 PM PDT)
        result.Count.ShouldBe(2);
    }

    private static CronJob CreateJob(string id) => new()
    {
        Id = JobId.From(id),
        Name = $"Job {id}",
        Schedule = "*/5 * * * *",
        ActionType = "agent-prompt",
        AgentId = AgentId.From("test-agent"),
        Enabled = true,
        CreatedBy = "test",
        CreatedAt = DateTimeOffset.UtcNow
    };
}

using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Regression coverage for #3546: the missed-run scan filtered candidate jobs on
/// <c>Enabled</c> and <c>Schedule</c> only and never consulted <see cref="CronJob.ExpiresAt"/>.
/// A job past its expiry - whose real fires <c>CronScheduler</c> correctly suppresses via the
/// #2634 predicate - still had its entire post-expiry occurrence window scanned and written to
/// run history as missed runs on every gateway start (measured: 34 warnings in ~1s across six
/// expired jobs, three of which hit the 100-occurrence cap).
///
/// <para>Every test here asserts an observable: the missed-run set, the rows in run history, or
/// the presence/absence of a specific log line. None asserts merely that a field was read.</para>
/// </summary>
public sealed class MissedRunExpiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// AC1: a job whose expiry elapsed before the scan window even opened records ZERO missed
    /// runs. Without the guard the six-hour gap between the last run and now yields the full
    /// 100-occurrence cap of five-minute slots.
    /// </summary>
    [Fact]
    public void GetMissedRuns_JobExpiredBeforeWindow_ReturnsEmpty()
    {
        var job = CreateJob("expired") with
        {
            Schedule = "*/5 * * * *",
            LastRunAt = new DateTimeOffset(2026, 6, 11, 6, 0, 0, TimeSpan.Zero),
            ExpiresAt = new DateTimeOffset(2026, 6, 11, 6, 0, 0, TimeSpan.Zero)
        };

        MissedRunDetectionService.GetMissedRuns(job, Now).ShouldBeEmpty();

        // Control: the identical job with no expiry yields the full six-hour window, so the
        // emptiness above is caused by the clamp and not by an empty window. The walk is
        // half-open on both ends, so this is 06:05 .. 11:55 inclusive = 71 slots - under the
        // 100-occurrence cap, which therefore never binds here. The set is pinned exactly
        // rather than merely non-empty: a clamp that was over-broad by even one slot must fail
        // this control, and "greater than zero" would not catch that.
        var unexpiring = job with { ExpiresAt = null };
        var control = MissedRunDetectionService.GetMissedRuns(unexpiring, Now);

        control.Count.ShouldBe(71);
        control[0].ShouldBe(new DateTimeOffset(2026, 6, 11, 6, 5, 0, TimeSpan.Zero));
        control[^1].ShouldBe(new DateTimeOffset(2026, 6, 11, 11, 55, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// AC1: no cap-truncation warning is emitted for an expired job whose window would otherwise
    /// exceed the 100-occurrence cap. Six hours of five-minute slots is 72 short of a full day but
    /// well past the cap, and before the fix this job logged the misleading
    /// "was truncated at the 100-occurrence cap" line on every restart.
    /// </summary>
    [Fact]
    public void WasTruncated_ExpiredJobExceedingCap_IsFalse()
    {
        var job = CreateJob("expired-cap") with
        {
            Schedule = "*/5 * * * *",
            LastRunAt = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero),
            ExpiresAt = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero)
        };

        MissedRunDetectionService.GetMissedRuns(job, Now).ShouldBeEmpty();
        MissedRunDetectionService.WasTruncated(job, Now).ShouldBeFalse();

        // Control: without the expiry the same job both fills the cap and reports truncation.
        var unexpiring = job with { ExpiresAt = null };
        MissedRunDetectionService.GetMissedRuns(unexpiring, Now)
            .Count.ShouldBe(MissedRunDetectionService.MaxMissedRunsPerJob);
        MissedRunDetectionService.WasTruncated(unexpiring, Now).ShouldBeTrue();
    }

    /// <summary>
    /// AC3: a job that expires PARTWAY through the scan window keeps the occurrences strictly
    /// before <c>ExpiresAt</c> and drops every one at or after it. Expiry clamps the window's upper
    /// bound rather than discarding the job wholesale - the mirror of #2554's lower-bound clamp.
    ///
    /// <para>This is also the mutation target named in the non-vacuity clause: inverting the
    /// comparison redden this test while leaving the AC4 control green.</para>
    /// </summary>
    [Fact]
    public void GetMissedRuns_ExpiryPartwayThroughWindow_ClampsUpperBound()
    {
        var job = CreateJob("partial") with
        {
            Schedule = "*/5 * * * *",
            LastRunAt = new DateTimeOffset(2026, 6, 11, 11, 0, 0, TimeSpan.Zero),
            ExpiresAt = new DateTimeOffset(2026, 6, 11, 11, 30, 0, TimeSpan.Zero)
        };

        var result = MissedRunDetectionService.GetMissedRuns(job, Now);

        // 11:05 .. 11:25 inclusive. 11:30 is excluded: it lands exactly on the expiry instant and
        // the >= in the expiry predicate makes that instant already past.
        result.Count.ShouldBe(5);
        result[0].ShouldBe(new DateTimeOffset(2026, 6, 11, 11, 5, 0, TimeSpan.Zero));
        result[^1].ShouldBe(new DateTimeOffset(2026, 6, 11, 11, 25, 0, TimeSpan.Zero));
        result.ShouldAllBe(r => r < job.ExpiresAt!.Value);

        // Without the clamp the same window runs on to 11:55.
        var unexpiring = job with { ExpiresAt = null };
        MissedRunDetectionService.GetMissedRuns(unexpiring, Now).Count.ShouldBe(11);
    }

    /// <summary>
    /// AC4: an expiry in the FUTURE is not an expiry. The ceiling clamps to <c>now</c>, so the
    /// job's missed-run set is identical to the same job with no expiry at all.
    /// </summary>
    [Fact]
    public void GetMissedRuns_FutureExpiry_MatchesNonExpiringJob()
    {
        var job = CreateJob("future-expiry") with
        {
            Schedule = "*/5 * * * *",
            LastRunAt = new DateTimeOffset(2026, 6, 11, 11, 0, 0, TimeSpan.Zero),
            ExpiresAt = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        var withFutureExpiry = MissedRunDetectionService.GetMissedRuns(job, Now);
        var withoutExpiry = MissedRunDetectionService.GetMissedRuns(job with { ExpiresAt = null }, Now);

        withFutureExpiry.Count.ShouldBe(11);
        withFutureExpiry.ShouldBe(withoutExpiry);
    }

    /// <summary>
    /// AC4: <c>ExpiresAt = null</c> is byte-identical to pre-#3546 behaviour, INCLUDING the #2554
    /// activation clamp. The floor is still max(LastRunAt, ScheduleActivatedAt) and the ceiling is
    /// still <c>now</c>.
    /// </summary>
    [Fact]
    public void GetMissedRuns_NullExpiry_PreservesActivationClampBehaviour()
    {
        var job = CreateJob("null-expiry") with
        {
            Schedule = "*/5 * * * *",
            LastRunAt = new DateTimeOffset(2026, 6, 11, 3, 0, 0, TimeSpan.Zero),
            ScheduleActivatedAt = new DateTimeOffset(2026, 6, 11, 11, 0, 0, TimeSpan.Zero),
            ExpiresAt = null
        };

        var result = MissedRunDetectionService.GetMissedRuns(job, Now);

        var expected = Enumerable.Range(1, 11)
            .Select(i => new DateTimeOffset(2026, 6, 11, 11, i * 5, 0, TimeSpan.Zero))
            .ToList();

        result.ShouldBe(expected);
        MissedRunDetectionService.WasTruncated(job, Now).ShouldBeFalse();
    }

    /// <summary>
    /// AC3 interaction: both clamps applied at once. The floor is the activation stamp (#2554) and
    /// the ceiling is the expiry (#3546); only the slots between them survive.
    /// </summary>
    [Fact]
    public void GetMissedRuns_ActivationFloorAndExpiryCeiling_BothApply()
    {
        var job = CreateJob("both-clamps") with
        {
            Schedule = "*/5 * * * *",
            LastRunAt = new DateTimeOffset(2026, 6, 11, 3, 0, 0, TimeSpan.Zero),
            ScheduleActivatedAt = new DateTimeOffset(2026, 6, 11, 11, 0, 0, TimeSpan.Zero),
            ExpiresAt = new DateTimeOffset(2026, 6, 11, 11, 20, 0, TimeSpan.Zero)
        };

        var result = MissedRunDetectionService.GetMissedRuns(job, Now);

        result.ShouldBe(
        [
            new DateTimeOffset(2026, 6, 11, 11, 5, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 11, 11, 10, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 11, 11, 15, 0, TimeSpan.Zero)
        ]);
    }

    /// <summary>
    /// AC1 end to end, against a controllable <see cref="TimeProvider"/>: an expired job with
    /// <c>catchUp: true</c> whose window would blow through the cap writes NO missed rows, logs NO
    /// missed-run warning, logs NO cap-truncation warning, and never enters the catch-up branch.
    ///
    /// <para><c>CronScheduler</c> is sealed, so the scan is handed a null scheduler: had the
    /// catch-up branch been entered, the resulting <c>NullReferenceException</c> would have been
    /// caught and logged as "Catch-up execution failed", which is asserted absent.</para>
    /// </summary>
    [Fact]
    public async Task StartAsync_ExpiredJob_RecordsNothingAndWarnsNothing()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        var scanTime = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(scanTime);

        var created = await context.Store.CreateAsync(CronStoreTestContext.CreateJob("expired-e2e") with
        {
            Schedule = "*/5 * * * *",
            ExpiresAt = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero),
            Metadata = new Dictionary<string, object?> { ["catchUp"] = "true" }
        });

        await context.Store.RecordRunFinalizationAsync(
            created.Id, new DateTimeOffset(2026, 6, 9, 23, 55, 0, TimeSpan.Zero), CronRunStatus.Ok, null);

        var logger = new CapturingLogger();
        var service = new MissedRunDetectionService(context.Store, null!, logger, timeProvider);

        await service.StartAsync(CancellationToken.None);

        var history = await context.Store.GetRunHistoryAsync(created.Id, limit: 500);
        history.Where(r => r.Status == MissedRunDetectionService.MissedStatus).ShouldBeEmpty();

        logger.Messages.ShouldNotContain(m => m.Contains("missed scheduled run", StringComparison.Ordinal));
        logger.Messages.ShouldNotContain(m => m.Contains("was truncated at the", StringComparison.Ordinal));
        logger.Messages.ShouldNotContain(m => m.Contains("Triggering catch-up execution", StringComparison.Ordinal));
        logger.Messages.ShouldNotContain(m => m.Contains("Catch-up execution failed", StringComparison.Ordinal));

        // Non-vacuity: the same stored job with the expiry lifted DOES produce a capped, truncated
        // missed-run set, so the silence above is the guard and not an empty window.
        var reloaded = (await context.Store.GetAsync(created.Id))!;
        MissedRunDetectionService.GetMissedRuns(reloaded with { ExpiresAt = null }, scanTime)
            .Count.ShouldBe(MissedRunDetectionService.MaxMissedRunsPerJob);
        MissedRunDetectionService.WasTruncated(reloaded with { ExpiresAt = null }, scanTime).ShouldBeTrue();
    }

    /// <summary>
    /// AC4 end to end: the non-expiring control. A job with <c>ExpiresAt = null</c> still has its
    /// missed runs detected and recorded, and still triggers catch-up. This is the direction that
    /// must not regress - an agent that should have run and did not is silent data loss - and it
    /// is the test the non-vacuity clause requires to stay GREEN under the expiry mutation.
    /// </summary>
    [Fact]
    public async Task StartAsync_NonExpiringJob_StillRecordsMissedRuns()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        var scanTime = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(scanTime);

        var created = await context.Store.CreateAsync(CronStoreTestContext.CreateJob("no-expiry-e2e") with
        {
            Schedule = "*/5 * * * *",
            ExpiresAt = null
        });

        await context.Store.RecordRunFinalizationAsync(
            created.Id, new DateTimeOffset(2026, 6, 11, 11, 28, 0, TimeSpan.Zero), CronRunStatus.Ok, null);

        var logger = new CapturingLogger();
        var service = new MissedRunDetectionService(context.Store, null!, logger, timeProvider);

        await service.StartAsync(CancellationToken.None);

        var missed = (await context.Store.GetRunHistoryAsync(created.Id, limit: 500))
            .Where(r => r.Status == MissedRunDetectionService.MissedStatus)
            .ToList();

        missed.Count.ShouldBeGreaterThan(0);
        logger.Messages.ShouldContain(m => m.Contains("missed scheduled run", StringComparison.Ordinal));
    }

    /// <summary>
    /// AC4 + #2477: idempotency is unchanged for a non-expiring job. Two scans over the same
    /// window converge on one row per occurrence rather than duplicating history.
    /// </summary>
    [Fact]
    public async Task StartAsync_NonExpiringJobScannedTwice_RemainsIdempotent()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        var scanTime = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

        var created = await context.Store.CreateAsync(CronStoreTestContext.CreateJob("idempotent-e2e") with
        {
            Schedule = "*/5 * * * *",
            ExpiresAt = null
        });

        await context.Store.RecordRunFinalizationAsync(
            created.Id, new DateTimeOffset(2026, 6, 11, 11, 28, 0, TimeSpan.Zero), CronRunStatus.Ok, null);

        async Task<int> ScanAsync()
        {
            var service = new MissedRunDetectionService(
                context.Store, null!, new CapturingLogger(), new ManualTimeProvider(scanTime));
            await service.StartAsync(CancellationToken.None);
            return (await context.Store.GetRunHistoryAsync(created.Id, limit: 500))
                .Count(r => r.Status == MissedRunDetectionService.MissedStatus);
        }

        var afterFirst = await ScanAsync();
        var afterSecond = await ScanAsync();

        afterFirst.ShouldBeGreaterThan(0);
        afterSecond.ShouldBe(afterFirst);
    }

    /// <summary>
    /// AC2: the extracted predicate is the one the scheduler's own suppression sites use. A null
    /// expiry is never expired; the comparison is inclusive at the expiry instant.
    /// </summary>
    [Fact]
    public void IsExpired_SharedPredicate_MatchesSchedulerSemantics()
    {
        var expiry = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var job = CreateJob("predicate") with { ExpiresAt = expiry };

        CronJobExpiry.IsExpired(job with { ExpiresAt = null }, Now).ShouldBeFalse();
        CronJobExpiry.IsExpired(job, expiry.AddTicks(-1)).ShouldBeFalse();
        CronJobExpiry.IsExpired(job, expiry).ShouldBeTrue();
        CronJobExpiry.IsExpired(job, expiry.AddTicks(1)).ShouldBeTrue();
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
        CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)
    };

    private sealed class CapturingLogger : ILogger<MissedRunDetectionService>
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
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}

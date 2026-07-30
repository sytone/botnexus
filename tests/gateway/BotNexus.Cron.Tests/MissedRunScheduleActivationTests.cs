using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Regression coverage for #2554: the missed-run scan walked the job's <b>current</b> cron
/// expression forward from <c>LastRunAt</c>, which belongs to the job's <b>previous</b> schedule.
/// Editing a recurring job's schedule therefore manufactured up to 100 "missed" occurrences that
/// never existed, wrote them to run history, and for <c>catchUp: true</c> jobs fired the job.
///
/// These tests assert the observable outcome - the returned missed-run set, the rows written to
/// run history, and whether the catch-up execution path was entered - never merely that a
/// timestamp field was written.
/// </summary>
public sealed class MissedRunScheduleActivationTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// AC4: the pre-edit window produces zero missed runs. Job last ran at 03:00 under
    /// <c>0 3 * * *</c>; the schedule was changed to <c>*/5 * * * *</c> at 11:00. Without the
    /// clamp the scan returns the cap (100) of five-minute slots stretching back to 03:00.
    /// </summary>
    [Fact]
    public void GetMissedRuns_ScheduleEditedAfterLastRun_ExcludesPreEditWindow()
    {
        var job = CreateJob("edited") with
        {
            Schedule = "*/5 * * * *",
            LastRunAt = new DateTimeOffset(2026, 6, 11, 3, 0, 0, TimeSpan.Zero),
            ScheduleActivatedAt = new DateTimeOffset(2026, 6, 11, 11, 0, 0, TimeSpan.Zero)
        };

        var result = MissedRunDetectionService.GetMissedRuns(job, Now);

        // Only the twelve 5-minute slots inside (11:00, 12:00) are legitimate: 11:05 .. 11:55
        // plus 12:00 is excluded because it is not < now.
        result.Count.ShouldBe(11);
        result[0].ShouldBe(new DateTimeOffset(2026, 6, 11, 11, 5, 0, TimeSpan.Zero));
        result[^1].ShouldBe(new DateTimeOffset(2026, 6, 11, 11, 55, 0, TimeSpan.Zero));
        result.ShouldAllBe(r => r > job.ScheduleActivatedAt!.Value);
    }

    /// <summary>
    /// AC3: no occurrence earlier than the activation stamp, even when every slot the old
    /// schedule could have produced predates it.
    /// </summary>
    [Fact]
    public void GetMissedRuns_ActivationAfterAllOccurrences_ReturnsEmpty()
    {
        var job = CreateJob("edited-recent") with
        {
            Schedule = "*/5 * * * *",
            LastRunAt = new DateTimeOffset(2026, 6, 11, 3, 0, 0, TimeSpan.Zero),
            ScheduleActivatedAt = new DateTimeOffset(2026, 6, 11, 11, 59, 30, TimeSpan.Zero)
        };

        var result = MissedRunDetectionService.GetMissedRuns(job, Now);

        result.ShouldBeEmpty();
    }

    /// <summary>
    /// AC5: behaviour parity. A job whose schedule never changed (activation stamp null, i.e. the
    /// pre-existing-row / NULL-column case) produces exactly the same set as a job with no
    /// activation concept at all. Suppressing a legitimate missed run would be worse than the bug.
    /// </summary>
    [Fact]
    public void GetMissedRuns_NullActivation_MatchesLegacyLastRunAtBehaviour()
    {
        var job = CreateJob("legacy") with
        {
            Schedule = "*/5 * * * *",
            LastRunAt = new DateTimeOffset(2026, 6, 11, 11, 0, 0, TimeSpan.Zero),
            ScheduleActivatedAt = null
        };

        var result = MissedRunDetectionService.GetMissedRuns(job, Now);

        // Legacy expectation computed straight from LastRunAt: 11:05 .. 11:55 inclusive.
        var expected = Enumerable.Range(1, 11)
            .Select(i => new DateTimeOffset(2026, 6, 11, 11, i * 5, 0, TimeSpan.Zero))
            .ToList();

        result.ShouldBe(expected);
    }

    /// <summary>
    /// AC5 (second direction): an activation stamp that is OLDER than the last run must not move
    /// the floor at all - the clamp is max(LastRunAt, ScheduleActivatedAt), not a replacement.
    /// </summary>
    [Fact]
    public void GetMissedRuns_ActivationOlderThanLastRun_DetectsMissedRunsUnchanged()
    {
        var lastRun = new DateTimeOffset(2026, 6, 11, 11, 0, 0, TimeSpan.Zero);

        var withStamp = CreateJob("older-stamp") with
        {
            Schedule = "*/5 * * * *",
            LastRunAt = lastRun,
            ScheduleActivatedAt = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero)
        };
        var withoutStamp = withStamp with { ScheduleActivatedAt = null };

        var stamped = MissedRunDetectionService.GetMissedRuns(withStamp, Now);
        var unstamped = MissedRunDetectionService.GetMissedRuns(withoutStamp, Now);

        stamped.Count.ShouldBe(11);
        stamped.ShouldBe(unstamped);
    }

    /// <summary>
    /// The truncation warning and the scan must share one floor. Before the fix, a job whose
    /// schedule was just edited hit the 100-occurrence cap and logged a misleading truncation
    /// warning. With the shared floor there is nothing to truncate.
    /// </summary>
    [Fact]
    public void WasTruncated_ScheduleEditedAfterLastRun_UsesSameFloorAsScan()
    {
        var job = CreateJob("trunc") with
        {
            Schedule = "*/5 * * * *",
            LastRunAt = new DateTimeOffset(2026, 6, 10, 3, 0, 0, TimeSpan.Zero),
            ScheduleActivatedAt = new DateTimeOffset(2026, 6, 11, 11, 0, 0, TimeSpan.Zero)
        };

        MissedRunDetectionService.GetMissedRuns(job, Now).Count.ShouldBe(11);
        MissedRunDetectionService.WasTruncated(job, Now).ShouldBeFalse();

        // Same job with no stamp: a day of 5-minute slots blows straight through the cap.
        var unclamped = job with { ScheduleActivatedAt = null };
        MissedRunDetectionService.GetMissedRuns(unclamped, Now)
            .Count.ShouldBe(MissedRunDetectionService.MaxMissedRunsPerJob);
        MissedRunDetectionService.WasTruncated(unclamped, Now).ShouldBeTrue();
    }

    /// <summary>
    /// AC6: a <c>catchUp: true</c> job whose only "missed" slots predate the schedule change must
    /// not fire, and must not write missed rows. The catch-up branch is guarded by
    /// <c>missedRuns.Count > 0 &amp;&amp; HasCatchUp(job)</c>, so an empty missed set is exactly the
    /// condition under which <c>RunNowAsync</c> cannot be reached; the test asserts both the empty
    /// history and the absence of the catch-up log lines. <c>CronScheduler</c> is sealed, so the
    /// scan is handed a null scheduler: had the branch been entered, the resulting
    /// <c>NullReferenceException</c> would have been caught and logged as "Catch-up execution
    /// failed", which is asserted absent.
    /// </summary>
    [Fact]
    public async Task StartAsync_ScheduleEditedAfterLastRun_NoMissedRowsAndNoCatchUp()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        var created = await context.Store.CreateAsync(CronStoreTestContext.CreateJob("catchup-edited") with
        {
            Schedule = "0 3 * * *",
            Metadata = new Dictionary<string, object?> { ["catchUp"] = "true" }
        });

        // A real run under the OLD schedule, then a real schedule edit through the store - which
        // is the only thing that stamps the activation instant.
        await context.Store.RecordRunFinalizationAsync(
            created.Id, DateTimeOffset.UtcNow.AddHours(-9), CronRunStatus.Ok, null);
        await context.Store.UpdateDefinitionAsync(created with { Schedule = "*/5 * * * *" });

        var logger = new CapturingLogger();
        var service = new MissedRunDetectionService(context.Store, null!, logger);

        await service.StartAsync(CancellationToken.None);

        var history = await context.Store.GetRunHistoryAsync(created.Id, limit: 500);
        history.Where(r => r.Status == MissedRunDetectionService.MissedStatus).ShouldBeEmpty();

        // Direct observable on the scan itself, independent of any log formatting.
        MissedRunDetectionService
            .GetMissedRuns((await context.Store.GetAsync(created.Id))!, DateTimeOffset.UtcNow)
            .ShouldBeEmpty();

        logger.Messages.ShouldNotContain(m => m.Contains("Triggering catch-up execution", StringComparison.Ordinal));
        logger.Messages.ShouldNotContain(m => m.Contains("Catch-up execution failed", StringComparison.Ordinal));
    }

    /// <summary>
    /// AC5 at the store level: a job whose schedule was never edited still has missed runs
    /// detected and still triggers catch-up. This is the direction that must NOT regress -
    /// an agent that should have run and did not is silent data loss.
    /// </summary>
    [Fact]
    public async Task StartAsync_ScheduleNeverChanged_StillRecordsMissedRunsAndCatchesUp()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        var created = await context.Store.CreateAsync(CronStoreTestContext.CreateJob("catchup-unchanged") with
        {
            Schedule = "*/5 * * * *",
            Metadata = new Dictionary<string, object?> { ["catchUp"] = "true" }
        });

        await context.Store.RecordRunFinalizationAsync(
            created.Id, DateTimeOffset.UtcNow.AddMinutes(-32), CronRunStatus.Ok, null);

        // A definition edit that leaves Schedule and TimeZone alone must not stamp an activation.
        await context.Store.UpdateDefinitionAsync(
            (await context.Store.GetAsync(created.Id))! with { Name = "renamed only" });

        var logger = new CapturingLogger();
        var service = new MissedRunDetectionService(context.Store, null!, logger);

        await service.StartAsync(CancellationToken.None);

        var missed = (await context.Store.GetRunHistoryAsync(created.Id, limit: 500))
            .Where(r => r.Status == MissedRunDetectionService.MissedStatus)
            .ToList();

        missed.Count.ShouldBeGreaterThan(0);

        // The scan reported each occurrence as missed - i.e. the unchanged-schedule path was not
        // suppressed by the #2554 clamp.
        logger.Messages.ShouldContain(m => m.Contains("missed scheduled run", StringComparison.Ordinal));
    }

    /// <summary>
    /// AC2 (security): a caller-supplied activation stamp on the create path is ignored, not
    /// honoured. A forward-dated stamp accepted from a payload would suppress every missed run;
    /// a back-dated one on a catchUp job would force an immediate agent-prompt / shell execution.
    /// The observable is the missed-run set the scan produces afterwards.
    /// </summary>
    [Fact]
    public async Task CreateAsync_CallerSuppliedScheduleActivatedAt_IsIgnored()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        var spoofed = CronStoreTestContext.CreateJob("spoof") with
        {
            Schedule = "*/5 * * * *",
            // A caller trying to claim the schedule only just became active, which would silence
            // the scan entirely.
            ScheduleActivatedAt = DateTimeOffset.UtcNow.AddYears(50)
        };

        var created = await context.Store.CreateAsync(spoofed);
        var reloaded = (await context.Store.GetAsync(created.Id))!;

        reloaded.ScheduleActivatedAt.ShouldBeNull();

        // Observable proof: the scan still reports the missed runs it would have reported today.
        await context.Store.RecordRunFinalizationAsync(
            created.Id, DateTimeOffset.UtcNow.AddMinutes(-32), CronRunStatus.Ok, null);
        var afterRun = (await context.Store.GetAsync(created.Id))!;

        MissedRunDetectionService
            .GetMissedRuns(afterRun, DateTimeOffset.UtcNow)
            .Count.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// AC2 (security), update path: a caller-supplied stamp on an update is discarded, and the
    /// store stamps its own value only because Schedule actually changed. A caller cannot
    /// back-date the stamp to replay slots, nor forward-date it to silence legitimate ones.
    /// </summary>
    [Fact]
    public async Task UpdateDefinitionAsync_CallerSuppliedScheduleActivatedAt_IsIgnored()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        var created = await context.Store.CreateAsync(CronStoreTestContext.CreateJob("spoof-update") with
        {
            Schedule = "0 3 * * *"
        });
        await context.Store.RecordRunFinalizationAsync(
            created.Id, DateTimeOffset.UtcNow.AddHours(-9), CronRunStatus.Ok, null);

        var backdated = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var saved = await context.Store.UpdateDefinitionAsync(
            (await context.Store.GetAsync(created.Id))! with
            {
                Schedule = "*/5 * * * *",
                ScheduleActivatedAt = backdated
            });

        saved.ShouldNotBeNull();
        saved!.ScheduleActivatedAt.ShouldNotBeNull();
        saved.ScheduleActivatedAt!.Value.ShouldNotBe(backdated);

        // Observable proof: the back-dated value was not used as the floor, so the nine hours of
        // pre-edit five-minute slots are NOT replayed.
        MissedRunDetectionService
            .GetMissedRuns(saved, DateTimeOffset.UtcNow)
            .ShouldAllBe(r => r >= saved.ScheduleActivatedAt!.Value);
    }

    /// <summary>
    /// A time-zone-only edit changes which wall-clock instants the expression produces, so it must
    /// stamp the activation too.
    /// </summary>
    [Fact]
    public async Task UpdateDefinitionAsync_TimeZoneOnlyChange_StampsActivation()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        var created = await context.Store.CreateAsync(CronStoreTestContext.CreateJob("tz-edit") with
        {
            Schedule = "0 3 * * *",
            TimeZone = null
        });

        var saved = await context.Store.UpdateDefinitionAsync(
            created with { TimeZone = "America/Los_Angeles" });

        saved.ShouldNotBeNull();
        saved!.ScheduleActivatedAt.ShouldNotBeNull();
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

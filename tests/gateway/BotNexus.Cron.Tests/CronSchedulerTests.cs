using System.Reflection;
using BotNexus.Cron.Actions;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

public sealed class CronSchedulerTests
{
    [Fact]
    public async Task Scheduler_ExecutesDueJobs()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        action.ExecutionCount.ShouldBe(1);
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history.ShouldHaveSingleItem().Status.ShouldBe("ok");
    }

    [Fact]
    public async Task Scheduler_SkipsDisabledJobs()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action", enabled: false) with
        {
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        action.ExecutionCount.ShouldBe(0);
        (await context.Store.GetRunHistoryAsync(JobId.From("job-1"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Scheduler_RecordsRunOnSuccess()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe("ok");
        var updated = await context.Store.GetAsync(JobId.From("job-1"));
        updated!.LastRunStatus.ShouldBe("ok");
        updated.LastRunError.ShouldBeNull();
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history.ShouldHaveSingleItem().Status.ShouldBe("ok");
    }

    [Fact]
    public async Task Scheduler_RecordsRunSessionId_WhenActionCreatesSession()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new SessionRecordingAction("test-action", "cron:job-1:session-1");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe("ok");
        run.SessionId!.Value.Value.ShouldBe("cron:job-1:session-1");
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history.ShouldHaveSingleItem().SessionId!.Value.Value.ShouldBe("cron:job-1:session-1");
    }

    [Fact]
    public async Task Scheduler_RecordsErrorOnFailure()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new ThrowingAction("test-action", "boom");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe("error");
        run.Error.ShouldBe("boom");
        var updated = await context.Store.GetAsync(JobId.From("job-1"));
        updated!.LastRunStatus.ShouldBe("error");
        updated.LastRunError.ShouldNotBeNull();
        updated.LastRunError.ShouldContain("boom");
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        var entry = history.ShouldHaveSingleItem();
        entry.Status.ShouldBe("error");
        entry.Error.ShouldBe("boom");
    }

    [Fact]
    public async Task Scheduler_WebhookAction_RecordsError_NotSilentSuccess()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "webhook") with
        {
            WebhookUrl = "https://example.test/hook"
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [new WebhookAction()]);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe("error");
        run.Error.ShouldNotBeNull();
        run.Error!.ToLowerInvariant().ShouldContain("not implemented");
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        history.ShouldHaveSingleItem().Status.ShouldBe("error");
    }

    [Fact]
    public async Task Scheduler_CorrectsStaleFutureNextRunAt_WhenScheduleChangedToFireSooner()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        // Create a job with NextRunAt set far in the future (simulates a schedule
        // that was updated but NextRunAt wasn't recomputed).
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            Schedule = "* * * * *",
            NextRunAt = DateTimeOffset.UtcNow.AddDays(365)
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        // The schedule says every minute, but NextRunAt is a year out.
        // Scheduler should detect the mismatch and correct NextRunAt.
        await InvokeProcessTickAsync(scheduler);

        var updated = await context.Store.GetAsync(JobId.From("job-1"));
        updated!.NextRunAt.ShouldNotBeNull();
        updated.NextRunAt!.Value.ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2),
            "NextRunAt should be corrected to the next occurrence from now");
    }

    [Fact]
    public async Task Scheduler_FiresOnNextTick_AfterCorrectedNextRunAtBecomesDue()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        // Job with stale NextRunAt (365 days out) and a "* * * * *" schedule.
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            Schedule = "* * * * *",
            NextRunAt = DateTimeOffset.UtcNow.AddDays(365)
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        // First tick corrects NextRunAt to the next minute.
        await InvokeProcessTickAsync(scheduler);
        action.ExecutionCount.ShouldBe(0, "corrected NextRunAt is still in the future");

        // Simulate time passing: set NextRunAt to the past.
        var corrected = await context.Store.GetAsync(JobId.From("job-1"));
        corrected.ShouldNotBeNull();
        corrected!.NextRunAt.ShouldNotBeNull();
        corrected.NextRunAt!.Value.ShouldBeLessThan(DateTimeOffset.UtcNow.AddDays(364),
            "NextRunAt should have been corrected from 365 days out");

        await context.Store.SetNextRunAtAsync(corrected.Id, DateTimeOffset.UtcNow.AddMinutes(-1));

        // Second tick: job fires because NextRunAt is now in the past.
        await InvokeProcessTickAsync(scheduler);
        action.ExecutionCount.ShouldBe(1, "job should fire after corrected NextRunAt becomes due");
    }

    [Fact]
    public async Task Scheduler_DoesNotCorrectNextRunAt_WhenItMatchesSchedule()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        // Set NextRunAt to 2 minutes from now. Schedule is every 5 minutes.
        // The computed next occurrence may be up to 5 minutes out, which could be
        // sooner or later than 2 minutes. We use a distant NextRunAt that is still
        // consistent: schedule "0 0 1 1 *" (Jan 1 midnight) with NextRunAt next Jan 1.
        var nextJan1 = new DateTimeOffset(DateTimeOffset.UtcNow.Year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            Schedule = "0 0 1 1 *",
            NextRunAt = nextJan1
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        action.ExecutionCount.ShouldBe(0, "job is not due yet");
        var updated = await context.Store.GetAsync(JobId.From("job-1"));
        updated!.NextRunAt.ShouldBe(nextJan1, "NextRunAt should not change when it matches the schedule");
    }

    [Fact]
    public async Task Scheduler_UsesJobTimeZone_WhenComputingNextRunAt()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        // Schedule "0 12 * * *" = noon daily. With UTC, NextRunAt would be noon UTC.
        // With a timezone like Pacific (UTC-7/UTC-8), noon Pacific is 19:00 or 20:00 UTC.
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            Schedule = "0 12 * * *",
            TimeZone = "America/Los_Angeles"
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        var updated = await context.Store.GetAsync(JobId.From("job-1"));
        updated!.NextRunAt.ShouldNotBeNull();

        // The next occurrence in Pacific should be at noon Pacific time.
        // In UTC, that's either 19:00 or 20:00 depending on DST.
        var pacificTz = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var localNext = TimeZoneInfo.ConvertTime(updated.NextRunAt!.Value, pacificTz);
        localNext.Hour.ShouldBe(12, "the cron expression should be interpreted in Pacific time");
        localNext.Minute.ShouldBe(0);
    }

    [Fact]
    public async Task Scheduler_FallsBackToUtc_WhenTimeZoneInvalid()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            Schedule = "0 12 * * *",
            TimeZone = "Invalid/Timezone"
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        var updated = await context.Store.GetAsync(JobId.From("job-1"));
        updated!.NextRunAt.ShouldNotBeNull();
        // With UTC fallback, the next occurrence should be at 12:00 UTC
        updated.NextRunAt!.Value.Hour.ShouldBe(12);
    }

    [Fact]
    public async Task Scheduler_ManualRunDoesNotClobberUpdatedSchedule()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            Schedule = "*/5 * * * *",
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(3)
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        // Update the schedule while the job exists
        var updated = job with
        {
            Schedule = "0 0 1 1 *"
        };
        await context.Store.UpdateDefinitionAsync(updated);
        await context.Store.SetNextRunAtAsync(updated.Id, new DateTimeOffset(DateTimeOffset.UtcNow.Year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero));

        // Manual run should not clobber the updated NextRunAt
        await scheduler.RunNowAsync(JobId.From("job-1"));

        var afterRun = await context.Store.GetAsync(JobId.From("job-1"));
        afterRun!.LastRunStatus.ShouldBe("ok");
        // NextRunAt should reflect the updated "0 0 1 1 *" schedule, not the old "*/5"
        afterRun.NextRunAt.ShouldNotBeNull();
        afterRun.NextRunAt!.Value.Month.ShouldBe(1);
        afterRun.NextRunAt!.Value.Day.ShouldBe(1);
    }

    [Fact]
    public async Task Scheduler_OneJobFailure_DoesNotPreventOtherJobsFromRunning()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var failAction = new ThrowingAction("fail-action", "kaboom");
        var okAction = new RecordingAction("ok-action");

        var job1 = CronStoreTestContext.CreateJob("job-fail", actionType: "fail-action") with
        {
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var job2 = CronStoreTestContext.CreateJob("job-ok", actionType: "ok-action") with
        {
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        await context.Store.CreateAsync(job1);
        await context.Store.CreateAsync(job2);

        var scheduler = CreateScheduler(context.Store, [failAction, okAction]);

        await InvokeProcessTickAsync(scheduler);

        okAction.ExecutionCount.ShouldBe(1,
            "the second job should still run even though the first threw");
        var failedRun = await context.Store.GetRunHistoryAsync(JobId.From("job-fail"));
        failedRun.ShouldHaveSingleItem().Status.ShouldBe("error");
    }

    [Fact]
    public async Task Scheduler_MultipleDueJobs_AllFire()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        for (var i = 1; i <= 3; i++)
        {
            var job = CronStoreTestContext.CreateJob($"job-{i}", actionType: "test-action") with
            {
                NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            await context.Store.CreateAsync(job);
        }

        var scheduler = CreateScheduler(context.Store, [action]);
        await InvokeProcessTickAsync(scheduler);

        action.ExecutionCount.ShouldBe(3, "all three due jobs should fire");
    }

    [Fact]
    public async Task Scheduler_JobWithInvalidSchedule_SkipsWithoutPoisoningLoop()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        var badJob = CronStoreTestContext.CreateJob("bad-job", actionType: "test-action") with
        {
            Schedule = "not a cron expression",
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var goodJob = CronStoreTestContext.CreateJob("good-job", actionType: "test-action") with
        {
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        await context.Store.CreateAsync(badJob);
        await context.Store.CreateAsync(goodJob);

        var scheduler = CreateScheduler(context.Store, [action]);
        await InvokeProcessTickAsync(scheduler);

        action.ExecutionCount.ShouldBe(1, "valid job should still fire");
        var goodHistory = await context.Store.GetRunHistoryAsync(JobId.From("good-job"));
        goodHistory.ShouldHaveSingleItem().Status.ShouldBe("ok");
    }

    [Fact]
    public async Task Scheduler_ReenabledJob_WithPastNextRunAt_FiresImmediately()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        // Job was disabled, had a past NextRunAt. Now re-enabled.
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            Enabled = true
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        action.ExecutionCount.ShouldBe(1,
            "re-enabled job with past NextRunAt should fire immediately");
    }

    [Fact]
    public async Task RunNowAsync_NonexistentJob_Throws()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var scheduler = CreateScheduler(context.Store, [action]);

        var act = () => scheduler.RunNowAsync(JobId.From("nonexistent-job"));

        await act.ShouldThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task RunNowAsync_DisabledJob_StillRuns()
    {
        // Manual runs should bypass the enabled check — the user
        // explicitly asked to run it.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action", enabled: false);
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe("ok");
        action.ExecutionCount.ShouldBe(1);
    }

    [Fact]
    public async Task Scheduler_SyncConfiguredJobs_UpdatedSchedule_CorrectsStaleness()
    {
        // Config sync changes a schedule from yearly to every minute,
        // but doesn't recompute NextRunAt. The scheduler's stale-detection
        // in ProcessTickAsync should catch and correct it.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        // Pre-seed a job with a yearly schedule and distant NextRunAt
        var job = CronStoreTestContext.CreateJob("config-job", actionType: "test-action") with
        {
            Schedule = "0 0 1 1 *",
            NextRunAt = new DateTimeOffset(DateTimeOffset.UtcNow.Year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        await context.Store.CreateAsync(job);

        // Now simulate config sync changing the schedule to every minute
        // but NOT recomputing NextRunAt (the SyncConfiguredJobs bug)
        var synced = job with { Schedule = "* * * * *" };
        await context.Store.UpdateDefinitionAsync(synced);

        var scheduler = CreateScheduler(context.Store, [action]);
        await InvokeProcessTickAsync(scheduler);

        // ProcessTickAsync should detect the mismatch and correct NextRunAt
        var updated = await context.Store.GetAsync(JobId.From("config-job"));
        updated!.NextRunAt.ShouldNotBeNull();
        updated.NextRunAt!.Value.ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2),
            "scheduler should correct stale NextRunAt after config sync");
    }

    [Fact]
    public async Task Scheduler_SyncConfiguredJobs_NormalizesAgentChatAndPersistsModel()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var logger = new ListLogger<CronScheduler>();
        var options = new CronOptions
        {
            Jobs = new Dictionary<string, ConfiguredCronJob>
            {
                ["config-job"] = new()
                {
                    Name = "Config Prompt Job",
                    Schedule = "*/5 * * * *",
                    ActionType = "agent-chat",
                    AgentId = "agent-a",
                    Message = "hello",
                    Model = "openai/gpt-4.1",
                    Enabled = true
                }
            }
        };
        var scheduler = CreateScheduler(context.Store, [new RecordingAction("agent-prompt")], options, logger);

        await InvokeSyncConfiguredJobsAsync(scheduler, options);

        var stored = await context.Store.GetAsync(JobId.From("config-job"));
        stored.ShouldNotBeNull();
        stored!.ActionType.ShouldBe("agent-prompt");
        stored.Model.ShouldBe("openai/gpt-4.1");
    }

    [Fact]
    public async Task Scheduler_SyncConfiguredJobs_PersistsTemplateReferences()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var logger = new ListLogger<CronScheduler>();
        var options = new CronOptions
        {
            Jobs = new Dictionary<string, ConfiguredCronJob>
            {
                ["config-job"] = new()
                {
                    Name = "Config template job",
                    Schedule = "*/5 * * * *",
                    ActionType = "agent-prompt",
                    AgentId = "agent-a",
                    TemplateName = "daily-status",
                    TemplateParameters = new Dictionary<string, string?> { ["owner"] = "Hermes" },
                    Enabled = true
                }
            }
        };
        var scheduler = CreateScheduler(context.Store, [new RecordingAction("agent-prompt")], options, logger);

        await InvokeSyncConfiguredJobsAsync(scheduler, options);

        var stored = await context.Store.GetAsync(JobId.From("config-job"));
        stored.ShouldNotBeNull();
        stored!.TemplateName.ShouldBe("daily-status");
        stored.TemplateParameters.ShouldNotBeNull();
        stored.TemplateParameters!["owner"].ShouldBe("Hermes");
        stored.Message.ShouldBeNull();
    }

    [Fact]
    public async Task Scheduler_SyncConfiguredJobs_InvalidJobs_AreSkippedWithWarnings()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var logger = new ListLogger<CronScheduler>();
        var options = new CronOptions
        {
            Jobs = new Dictionary<string, ConfiguredCronJob>
            {
                ["missing-required"] = new()
                {
                    ActionType = "agent-prompt",
                    AgentId = "agent-a"
                },
                ["unknown-action"] = new()
                {
                    Schedule = "*/5 * * * *",
                    ActionType = "unknown",
                    AgentId = "agent-a",
                    Message = "test"
                }
            }
        };
        var scheduler = CreateScheduler(context.Store, [new RecordingAction("agent-prompt")], options, logger);

        await InvokeSyncConfiguredJobsAsync(scheduler, options);

        (await context.Store.ListAsync()).ShouldBeEmpty();
        logger.Messages.ShouldContain(message => message.Contains("Skipping configured cron job 'missing-required'", StringComparison.Ordinal));
        logger.Messages.ShouldContain(message => message.Contains("Skipping configured cron job 'unknown-action'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scheduler_CreateWithInvalidSchedule_SetsNullNextRunAt()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            Schedule = "garbage"
        };
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, [action]);
        await InvokeProcessTickAsync(scheduler);

        // Should not throw, should not fire, job should be untouched
        action.ExecutionCount.ShouldBe(0);
    }

    [Fact]
    public async Task Scheduler_MultipleDueJobs_RunConcurrently_NotSerially()
    {
        // Two jobs each take ~150ms. Serial execution would take >=300ms.
        // Concurrent execution should finish in <250ms.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new DelayedAction("slow-action", TimeSpan.FromMilliseconds(150));

        for (var i = 1; i <= 2; i++)
        {
            var job = CronStoreTestContext.CreateJob($"job-{i}", actionType: "slow-action") with
            {
                NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            await context.Store.CreateAsync(job);
        }

        var scheduler = CreateScheduler(context.Store, [action]);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await InvokeProcessTickAsync(scheduler);
        sw.Stop();

        action.ExecutionCount.ShouldBe(2, "both jobs must run");
        sw.ElapsedMilliseconds.ShouldBeLessThan(800,
            "concurrent execution of two 150ms jobs should complete well under 300ms");
    }

    [Fact]
    public async Task Scheduler_MultipleDueJobs_PartialFailure_AllRunAndFailuresRecorded()
    {
        // Three jobs: first fails, second and third succeed.
        // With concurrent execution, all three must run (not abort on first failure).
        await using var context = await CronStoreTestContext.CreateAsync();
        var failAction = new ThrowingAction("fail-action", "kaboom");
        var okAction = new RecordingAction("ok-action");

        var jobs = new[]
        {
            ("job-fail", "fail-action"),
            ("job-ok-1", "ok-action"),
            ("job-ok-2", "ok-action")
        };
        foreach (var (id, type) in jobs)
        {
            var job = CronStoreTestContext.CreateJob(id, actionType: type) with
            {
                NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            await context.Store.CreateAsync(job);
        }

        var scheduler = CreateScheduler(context.Store, [failAction, okAction]);
        await InvokeProcessTickAsync(scheduler);

        okAction.ExecutionCount.ShouldBe(2, "both ok jobs should run despite the first job failing");
        var failHistory = await context.Store.GetRunHistoryAsync(JobId.From("job-fail"));
        failHistory.ShouldHaveSingleItem().Status.ShouldBe("error");
        var ok1History = await context.Store.GetRunHistoryAsync(JobId.From("job-ok-1"));
        ok1History.ShouldHaveSingleItem().Status.ShouldBe("ok");
        var ok2History = await context.Store.GetRunHistoryAsync(JobId.From("job-ok-2"));
        ok2History.ShouldHaveSingleItem().Status.ShouldBe("ok");
    }

    [Fact]
    public async Task Scheduler_MultipleDueJobs_NextRunAt_UpdatedIndependently()
    {
        // Verify that concurrent runs each update their own NextRunAt independently.
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        for (var i = 1; i <= 3; i++)
        {
            var job = CronStoreTestContext.CreateJob($"job-{i}", actionType: "test-action") with
            {
                NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            await context.Store.CreateAsync(job);
        }

        var scheduler = CreateScheduler(context.Store, [action]);
        await InvokeProcessTickAsync(scheduler);

        action.ExecutionCount.ShouldBe(3);
        for (var i = 1; i <= 3; i++)
        {
            var updated = await context.Store.GetAsync(JobId.From($"job-{i}"));
            updated.ShouldNotBeNull();
            updated!.NextRunAt.ShouldNotBeNull(
                $"job-{i} NextRunAt should be updated after concurrent run");
            updated.NextRunAt!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(-5),
                $"job-{i} NextRunAt should be a future time");
        }
    }

    private static CronScheduler CreateScheduler(
        ICronStore store,
        IEnumerable<ICronAction> actions,
        CronOptions? options = null,
        ILogger<CronScheduler>? logger = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        return new CronScheduler(
            store,
            actions,
            scopeFactory,
            new StaticOptionsMonitor<CronOptions>(options ?? new CronOptions { Enabled = true, TickIntervalSeconds = 1 }),
            logger ?? NullLogger<CronScheduler>.Instance);
    }

    private static async Task InvokeProcessTickAsync(CronScheduler scheduler)
    {
        var method = typeof(CronScheduler).GetMethod("ProcessTickAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull();
        var task = method!.Invoke(scheduler, [CancellationToken.None]) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private static async Task InvokeSyncConfiguredJobsAsync(CronScheduler scheduler, CronOptions options)
    {
        var method = typeof(CronScheduler).GetMethod("SyncConfiguredJobsAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull();
        var task = method!.Invoke(scheduler, [options, CancellationToken.None]) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private sealed class DelayedAction(string actionType, TimeSpan delay) : ICronAction
    {
        private int _executionCount;
        public int ExecutionCount => _executionCount;
        public string ActionType => actionType;

        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            Interlocked.Increment(ref _executionCount);
        }
    }

    private sealed class RecordingAction(string actionType) : ICronAction
    {
        public int ExecutionCount { get; private set; }
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAction(string actionType, string message) : ICronAction
    {
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(message);
    }

    private sealed class SessionRecordingAction(string actionType, string sessionId) : ICronAction
    {
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            context.RecordSessionId(SessionId.From(sessionId));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Action that signals when it begins executing and then blocks until the supplied token is
    /// cancelled, at which point it throws <see cref="OperationCanceledException"/>. Lets a test
    /// abort an in-flight run via the host token (distinct from the per-job timeout).
    /// </summary>
    private sealed class AbortableAction(string actionType) : ICronAction
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ActionType => actionType;

        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            // Block until cancelled; Task.Delay observes the linked token and throws on cancel.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class ListLogger<T> : ILogger<T>
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
            => Messages.Add(formatter(state, exception));
    }

    // ── Job timeout tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task RunNow_TimesOut_RecordsTimedOutStatus()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new DelayedAction("test-action", TimeSpan.FromSeconds(10));
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);
        var options = new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 1 };
        var scheduler = CreateScheduler(context.Store, [action], options);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe("timed_out");
        run.Error!.ShouldContain("timeout");
        var updated = await context.Store.GetAsync(JobId.From("job-1"));
        updated!.LastRunStatus.ShouldBe("timed_out");
    }

    [Fact]
    public async Task RunNow_PerJobTimeout_OverridesDefault()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new DelayedAction("test-action", TimeSpan.FromSeconds(10));
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            Metadata = new Dictionary<string, object?> { ["timeoutSeconds"] = 1 }
        };
        await context.Store.CreateAsync(job);
        // Default is high but per-job override is low
        var options = new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 600 };
        var scheduler = CreateScheduler(context.Store, [action], options);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe("timed_out");
    }

    [Fact]
    public async Task RunNow_CompletesBeforeTimeout_ReturnsOk()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);
        var options = new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 60 };
        var scheduler = CreateScheduler(context.Store, [action], options);

        var run = await scheduler.RunNowAsync(JobId.From("job-1"));

        run.Status.ShouldBe("ok");
        action.ExecutionCount.ShouldBe(1);
    }

    [Fact]
    public async Task RunNow_TimeoutLogsWarning()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new DelayedAction("test-action", TimeSpan.FromSeconds(10));
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);
        var options = new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 1 };
        var logger = new ListLogger<CronScheduler>();
        var scheduler = CreateScheduler(context.Store, [action], options, logger);

        await scheduler.RunNowAsync(JobId.From("job-1"));

        logger.Messages.ShouldContain(m => m.Contains("timed out"));
    }

    // ── Run abort (host cancellation) tests #1501 ───────────────────────────────

    [Fact]
    public async Task RunNow_AbortedViaHostToken_RecordsErrorNotStuckRunning()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new AbortableAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);
        // High timeout so the per-job timeout never fires - the abort must come from the host token.
        var options = new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 600 };
        var scheduler = CreateScheduler(context.Store, [action], options);

        using var cts = new CancellationTokenSource();
        var runTask = scheduler.RunNowAsync(JobId.From("job-1"), cts.Token);

        // Wait until the action is actually executing, then abort via the host token.
        await action.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        // Cancellation semantics are preserved: the abort propagates to the caller.
        await Should.ThrowAsync<OperationCanceledException>(async () => await runTask);

        // ...but the run must be recorded as a failure, not left stuck in "running".
        var history = await context.Store.GetRunHistoryAsync(JobId.From("job-1"));
        var entry = history.ShouldHaveSingleItem();
        entry.Status.ShouldBe("error");
        entry.Status.ShouldNotBe("running");
        entry.Status.ShouldNotBe("ok");
        entry.Error.ShouldNotBeNull();
        entry.Error.ShouldContain("aborted");

        var updated = await context.Store.GetAsync(JobId.From("job-1"));
        updated!.LastRunStatus.ShouldBe("error");
        updated.LastRunError.ShouldNotBeNull();
        updated.LastRunError.ShouldContain("aborted");
    }

    [Fact]
    public async Task RunNow_AbortedViaHostToken_LogsWarning()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new AbortableAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);
        var options = new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 600 };
        var logger = new ListLogger<CronScheduler>();
        var scheduler = CreateScheduler(context.Store, [action], options, logger);

        using var cts = new CancellationTokenSource();
        var runTask = scheduler.RunNowAsync(JobId.From("job-1"), cts.Token);
        await action.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await runTask);

        logger.Messages.ShouldContain(m => m.Contains("aborted"));
        // The abort must NOT be logged as a timeout - those are distinct outcomes.
        logger.Messages.ShouldNotContain(m => m.Contains("timed out"));
    }
}

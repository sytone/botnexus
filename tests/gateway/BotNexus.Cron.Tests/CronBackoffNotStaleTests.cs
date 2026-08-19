using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Issue #3350: the scheduler's stale-<c>NextRunAt</c> correction must not drag a
/// <b>deliberate</b> backoff forward.
/// </summary>
/// <remarks>
/// <para>
/// Before this change, <c>NextRunAt</c> carried two different meanings - "the expression's next
/// occurrence, cached" and "the time this job asked to be woken" - and the correction branch
/// (<c>computedNext &lt; job.NextRunAt</c>) assumed the first unconditionally. The two cases were
/// indistinguishable to that branch <i>by construction</i>, so a self-paced job that backed off
/// was silently pulled back onto its expression's cadence.
/// </para>
/// <para>
/// The fix separates the meanings into two fields rather than trying to infer between them:
/// <see cref="CronJob.NextRunAt"/> stays the expression-derived cache the scheduler owns and
/// corrects, and <see cref="CronJob.BackoffUntil"/> is the job-authored floor the scheduler
/// honours but never moves. Every assertion below is on an <b>observable</b>: whether the action
/// ran, and what wake time is actually stored afterwards - never merely that a write occurred.
/// </para>
/// <para>
/// Time is injected via <see cref="FixedTimeProvider"/>; there are no wall-clock waits (#2589).
/// <see cref="Now"/> sits exactly on a minute boundary so the <c>*/1 * * * *</c> schedule used
/// throughout has a next occurrence of exactly <c>Now + 1 minute</c>, making the corrected value
/// assertable as an equality rather than a range.
/// </para>
/// </remarks>
public sealed class CronBackoffNotStaleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The single occurrence of <c>*/1 * * * *</c> strictly after <see cref="Now"/>.</summary>
    private static readonly DateTimeOffset NextOccurrence = Now.AddMinutes(1);

    // ── AC1 + AC3: a deliberate backoff is honoured, and is not moved ──────────────

    /// <summary>
    /// AC1/AC3, the canonical defect. The stored wake time is LATER than the expression's next
    /// occurrence and was written as a deliberate backoff. The pre-fix correction branch saw
    /// "computed is sooner than stored" and overwrote it, so the job woke at the expression's
    /// cadence instead of the one it asked for.
    /// </summary>
    /// <remarks>
    /// The assertion is on the resulting wake time, not on whether a write happened: a fix that
    /// merely skipped the write while still treating the job as due at <c>NextOccurrence</c>
    /// would satisfy a write-counting test and still ship the defect.
    /// </remarks>
    [Fact]
    public async Task DeliberateBackoff_IsNotDraggedForwardByTheStaleCorrection()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var backoffUntil = Now.AddMinutes(30);
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            NextRunAt = Now.AddMinutes(-5),
            BackoffUntil = backoffUntil
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        // The job asked not to be woken until 12:30. It was not woken.
        action.Invocations.ShouldBe(0);

        // And the wake time it asked for is still the wake time that is stored - the scheduler
        // honoured it rather than replacing it with the expression's next occurrence.
        var stored = await context.Store.GetAsync(JobId.From("job-1"));
        stored.ShouldNotBeNull();
        stored!.BackoffUntil.ShouldBe(backoffUntil);
    }

    /// <summary>
    /// The same defect in the shape the issue describes literally: the backoff writer also parked
    /// <c>NextRunAt</c> at the later instant. The correction branch IS allowed to rewrite
    /// <c>NextRunAt</c> here (it is the expression cache, and it is genuinely stale) - but the
    /// effective wake time is <c>max(NextRunAt, BackoffUntil)</c>, so correcting the cache must
    /// not make the job due.
    /// </summary>
    [Fact]
    public async Task StaleCorrection_MayRewriteTheCache_ButNotTheEffectiveWakeTime()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var backoffUntil = Now.AddMinutes(30);
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            NextRunAt = backoffUntil,
            BackoffUntil = backoffUntil
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        action.Invocations.ShouldBe(0);

        var stored = await context.Store.GetAsync(JobId.From("job-1"));
        stored.ShouldNotBeNull();
        stored!.BackoffUntil.ShouldBe(
            backoffUntil,
            "the correction may only move the expression cache, never the job-authored floor");
    }

    /// <summary>
    /// The backoff is a floor, not a schedule: once it has elapsed the job runs on its ordinary
    /// cadence. Without this, "honour the backoff" could be implemented as "never run again".
    /// </summary>
    [Fact]
    public async Task ElapsedBackoff_DoesNotSuppressADueJob()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            NextRunAt = Now.AddMinutes(-5),
            BackoffUntil = Now.AddMinutes(-1)
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        action.Invocations.ShouldBe(1);
    }

    /// <summary>
    /// A backoff is consumed by the run it deferred. Leaving a spent floor behind would be
    /// harmless arithmetically (it is in the past) but would leave the job carrying a stale
    /// claim about its own pacing that no later reader could distinguish from a live one.
    /// </summary>
    [Fact]
    public async Task ElapsedBackoff_IsClearedByTheRunItDeferred()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            NextRunAt = Now.AddMinutes(-5),
            BackoffUntil = Now.AddMinutes(-1)
        };
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        var stored = await context.Store.GetAsync(JobId.From("job-1"));
        stored.ShouldNotBeNull();
        stored!.BackoffUntil.ShouldBeNull();
    }

    // ── AC2: the existing stale correction is preserved ────────────────────────────

    /// <summary>
    /// AC2. A job with NO backoff whose stored wake time is stale relative to an edited
    /// expression is still corrected. This test fails if the correction branch is removed
    /// entirely - the stored value would stay at <c>Now + 1 hour</c>.
    /// </summary>
    [Fact]
    public async Task StaleNextRunAt_WithoutABackoff_IsStillCorrected()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            // Left over from a schedule that used to fire hourly; the definition now says */1.
            NextRunAt = Now.AddHours(1)
        };
        job.BackoffUntil.ShouldBeNull("this job never asked to be paced");
        await context.Store.CreateAsync(job);
        var scheduler = CreateScheduler(context.Store, [action]);

        await InvokeProcessTickAsync(scheduler);

        var stored = await context.Store.GetAsync(JobId.From("job-1"));
        stored.ShouldNotBeNull();
        stored!.NextRunAt.ShouldBe(
            NextOccurrence,
            "a stale cache must still be pulled back onto the current expression (#3350 AC2)");
    }

    /// <summary>
    /// The correction's other half: it must actually take effect on the job's dueness, not merely
    /// be written. A fix that skipped the write, or wrote it and then went on using the stale
    /// value to decide dueness, would pass the assertion above and still leave the job waiting for
    /// the wake time its edited expression no longer names.
    /// </summary>
    /// <remarks>
    /// Note that the corrected value can never itself be in the past: <c>NextRun(now, tz)</c> is
    /// strictly after <c>now</c> by construction, which is why the correction is observed on a
    /// later tick rather than the same one.
    /// </remarks>
    [Fact]
    public async Task StaleNextRunAt_OnceCorrected_MakesTheJobDueOnTheCorrectedInstant()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");
        // Stored value left over from an hourly schedule; the definition now says every minute.
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            NextRunAt = Now.AddHours(1)
        };
        await context.Store.CreateAsync(job);
        var clock = new FixedTimeProvider(Now);
        var scheduler = CreateScheduler(context.Store, [action], clock: clock);

        // First tick corrects the cache to 12:01 and does not run (12:01 is still in the future).
        await InvokeProcessTickAsync(scheduler);
        action.Invocations.ShouldBe(0);

        // Past the corrected instant but far short of the stale one: only a correction that
        // actually took can make this run.
        clock.Advance(TimeSpan.FromMinutes(2));
        await InvokeProcessTickAsync(scheduler);
        action.Invocations.ShouldBe(1);
    }

    // ── AC4 + AC5: the two meanings are distinguishable in the store ───────────────

    /// <summary>
    /// AC4. The distinction is a stored field, not an inference from a comparison: a backoff
    /// survives a round-trip and is readable as itself.
    /// </summary>
    [Fact]
    public async Task BackoffUntil_RoundTripsThroughTheStore_AndDefaultsToNull()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var backoffUntil = new DateTimeOffset(2027, 3, 4, 5, 6, 7, TimeSpan.Zero);
        await context.Store.CreateAsync(
            CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with { BackoffUntil = backoffUntil });
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-2", actionType: "test-action"));

        (await context.Store.GetAsync(JobId.From("job-1")))!.BackoffUntil.ShouldBe(backoffUntil);
        (await context.Store.GetAsync(JobId.From("job-2")))!.BackoffUntil.ShouldBeNull(
            "a job that never backed off must read as unpaced, not as paced-until-the-epoch");
    }

    /// <summary>
    /// A row written by a build that predates the column reads as "no backoff", so an upgrade
    /// cannot retroactively suppress an existing job. Same inert-default rule as #2554/#2634.
    /// </summary>
    [Fact]
    public async Task LegacyRowWithoutTheColumn_ReadsAsUnpacedAndStillRuns()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await WriteLegacyRowAsync(context.DbPath, "legacy-1");

        var loaded = await context.Store.GetAsync(JobId.From("legacy-1"));
        loaded.ShouldNotBeNull();
        loaded!.BackoffUntil.ShouldBeNull();

        var action = new RecordingAction("test-action");
        var scheduler = CreateScheduler(context.Store, [action]);
        await scheduler.RunNowAsync(JobId.From("legacy-1"));

        action.Invocations.ShouldBe(1);
    }

    /// <summary>
    /// AC5. <see cref="ICronStore.SetNextRunAtAsync"/> is still the narrow write #2133 made it:
    /// it moves the expression cache and touches nothing else - including the new column.
    /// </summary>
    [Fact]
    public async Task SetNextRunAtAsync_RemainsNarrow_AndDoesNotDisturbTheBackoff()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var backoffUntil = Now.AddMinutes(30);
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            BackoffUntil = backoffUntil,
            LastRunStatus = "ok"
        };
        await context.Store.CreateAsync(job);

        await context.Store.SetNextRunAtAsync(JobId.From("job-1"), NextOccurrence);

        var stored = await context.Store.GetAsync(JobId.From("job-1"));
        stored.ShouldNotBeNull();
        stored!.NextRunAt.ShouldBe(NextOccurrence);
        stored.BackoffUntil.ShouldBe(backoffUntil);
        stored.Schedule.ShouldBe(job.Schedule);
        stored.Message.ShouldBe(job.Message);
    }

    /// <summary>
    /// The mirror of AC5 for the new write, and the reason it is a separate method rather than an
    /// extra parameter on <see cref="ICronStore.SetNextRunAtAsync"/>: setting a backoff must not
    /// be able to move the expression cache, or the two meanings would be re-entangled at the
    /// very seam introduced to separate them.
    /// </summary>
    [Fact]
    public async Task SetBackoffUntilAsync_IsNarrow_AndDoesNotDisturbTheExpressionCache()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            NextRunAt = NextOccurrence
        };
        await context.Store.CreateAsync(job);

        var backoffUntil = Now.AddMinutes(30);
        await context.Store.SetBackoffUntilAsync(JobId.From("job-1"), backoffUntil);

        var stored = await context.Store.GetAsync(JobId.From("job-1"));
        stored.ShouldNotBeNull();
        stored!.BackoffUntil.ShouldBe(backoffUntil);
        stored.NextRunAt.ShouldBe(NextOccurrence);
        stored.Schedule.ShouldBe(job.Schedule);

        // ...and it clears, because a job that finished backing off must be able to say so.
        await context.Store.SetBackoffUntilAsync(JobId.From("job-1"), null);
        (await context.Store.GetAsync(JobId.From("job-1")))!.BackoffUntil.ShouldBeNull();
    }

    /// <summary>
    /// A backoff is scheduler-owned runtime bookkeeping, exactly like <c>NextRunAt</c>, so a
    /// concurrent controller/tool definition edit must not carry a stale copy of it back into the
    /// row (#2133). Without this, a user renaming a job would silently cancel its pacing.
    /// </summary>
    [Fact]
    public async Task UpdateDefinitionAsync_DoesNotClobberTheBackoff()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action");
        await context.Store.CreateAsync(job);
        var backoffUntil = Now.AddMinutes(30);
        await context.Store.SetBackoffUntilAsync(JobId.From("job-1"), backoffUntil);

        // A definition edit carrying the record's default (null) backoff, as a round-trip would.
        var updated = await context.Store.UpdateDefinitionAsync(job with { Name = "Renamed", BackoffUntil = null });

        updated.ShouldNotBeNull();
        updated!.Name.ShouldBe("Renamed");
        updated.BackoffUntil.ShouldBe(backoffUntil);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a row through a column list that omits <c>backoff_until</c> entirely, simulating a
    /// row persisted by a build that predates the column.
    /// </summary>
    private static async Task WriteLegacyRowAsync(string dbPath, string jobId)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cron_jobs (id, name, schedule, action_type, agent_id, message, enabled, system, created_by, created_at)
            VALUES ($id, $name, '*/1 * * * *', 'test-action', 'agent-a', 'legacy', 1, 0, 'test-agent', $createdAt)
            """;
        command.Parameters.AddWithValue("$id", jobId);
        command.Parameters.AddWithValue("$name", $"Legacy {jobId}");
        command.Parameters.AddWithValue("$createdAt", Now.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InvokeProcessTickAsync(CronScheduler scheduler)
    {
        var method = typeof(CronScheduler).GetMethod(
            "ProcessTickAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method.ShouldNotBeNull();
        var task = method!.Invoke(scheduler, [CancellationToken.None]) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private static CronScheduler CreateScheduler(
        ICronStore store,
        IEnumerable<ICronAction> actions,
        FixedTimeProvider? clock = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISessionStore>(Mock.Of<ISessionStore>());
        services.AddSingleton(Mock.Of<IConversationStore>());
        var provider = services.BuildServiceProvider();
        return new CronScheduler(
            store,
            actions,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(new CronOptions { Enabled = true, TickIntervalSeconds = 1 }),
            NullLogger<CronScheduler>.Instance,
            clock ?? new FixedTimeProvider(Now));
    }

    /// <summary>Deterministic, manually advanced clock. No wall-clock dependency (#2589).</summary>
    private sealed class FixedTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class RecordingAction(string actionType) : ICronAction
    {
        public string ActionType => actionType;

        public int Invocations { get; private set; }

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            Invocations++;
            return Task.CompletedTask;
        }
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

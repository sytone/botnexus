using System.Reflection;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #3659: the Phase 2 <c>Parallel.ForEachAsync</c> fan-out in <c>ProcessTickAsync</c> had no
/// per-job exception isolation, so a single <c>SQLITE_BUSY</c> on one job's
/// <c>RecordRunStartAsync</c> propagated out of the body, cancelled the remaining partitions and
/// silently dropped every other due job in that tick.
/// </summary>
/// <remarks>
/// #2410 established the "failures here must never abort the tick" policy for the reaper. These
/// tests pin the same policy for the fan-out: one job's store fault fails that job's run and no
/// other, is attributed to the job it belongs to, and still advances that job's <c>NextRunAt</c>
/// so the failure is neither an immediate re-fire nor a permanent skip.
/// </remarks>
public sealed class CronTickFanOutIsolationTests
{
    private const string FailingJobId = "job-2";

    [Fact]
    public async Task Tick_WhenOneJobsRunStartWriteThrows_StillRunsEverySiblingJob()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new CountingAction("test-action");
        var ids = await SeedDueJobsAsync(context, count: 5);

        var store = new FaultInjectingCronStore(context.Store, JobId.From(FailingJobId));
        var scheduler = CreateScheduler(store, action, new ListLogger<CronScheduler>());

        await InvokeProcessTickAsync(scheduler);

        store.Faults.ShouldBe(1, "the fault must actually have been injected");
        action.ExecutedJobIds.Count.ShouldBe(ids.Count - 1,
            "every due job except the faulted one must still have executed");
        action.ExecutedJobIds.ShouldNotContain(FailingJobId);
        foreach (var id in ids.Where(i => i != FailingJobId))
            action.ExecutedJobIds.ShouldContain(id, $"sibling job '{id}' was dropped by the tick");
    }

    [Fact]
    public async Task Tick_WhenOneJobsRunStartWriteThrows_StillReschedulesEverySiblingJob()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new CountingAction("test-action");
        var ids = await SeedDueJobsAsync(context, count: 5);

        var store = new FaultInjectingCronStore(context.Store, JobId.From(FailingJobId));
        var scheduler = CreateScheduler(store, action, new ListLogger<CronScheduler>());

        var tickStart = DateTimeOffset.UtcNow;
        await InvokeProcessTickAsync(scheduler);

        foreach (var id in ids.Where(i => i != FailingJobId))
        {
            var job = await context.Store.GetAsync(JobId.From(id));
            job.ShouldNotBeNull();
            job!.NextRunAt.ShouldNotBeNull($"sibling job '{id}' lost its reschedule");
            job.NextRunAt!.Value.ShouldBeGreaterThan(tickStart,
                $"sibling job '{id}' retained a stale NextRunAt and would re-fire immediately");
        }
    }

    [Fact]
    public async Task Tick_WhenAJobsRunStartWriteThrows_ThatJobIsNotLeftWithAStaleNextRunAt()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new CountingAction("test-action");
        await SeedDueJobsAsync(context, count: 5);

        var store = new FaultInjectingCronStore(context.Store, JobId.From(FailingJobId));
        var scheduler = CreateScheduler(store, action, new ListLogger<CronScheduler>());

        var tickStart = DateTimeOffset.UtcNow;
        await InvokeProcessTickAsync(scheduler);

        // AC3: neither an immediate re-fire (stale past NextRunAt) nor a permanent skip (null).
        var failed = await context.Store.GetAsync(JobId.From(FailingJobId));
        failed.ShouldNotBeNull();
        failed!.NextRunAt.ShouldNotBeNull("a failed run must not leave the job unscheduled forever");
        failed.NextRunAt!.Value.ShouldBeGreaterThan(tickStart,
            "a failed run must not leave a stale NextRunAt that re-fires on the very next tick");
    }

    [Fact]
    public async Task Tick_AttributesTheFailureToTheJobItBelongsTo_NotToAnAnonymousTickError()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new CountingAction("test-action");
        await SeedDueJobsAsync(context, count: 5);

        var logger = new ListLogger<CronScheduler>();
        var store = new FaultInjectingCronStore(context.Store, JobId.From(FailingJobId));
        var scheduler = CreateScheduler(store, action, logger);

        await InvokeProcessTickAsync(scheduler);

        // AC2: the JobId AND the job name must both appear, so the dropped work is identifiable
        // from the log line alone rather than from an anonymous "Cron scheduler tick failed."
        var attributed = logger.Messages.Where(m => m.Contains(FailingJobId, StringComparison.Ordinal)).ToList();
        attributed.ShouldNotBeEmpty("the per-job failure was never logged against its job id");
        attributed.ShouldContain(
            m => m.Contains($"Job {FailingJobId}", StringComparison.Ordinal),
            "the per-job failure log must name the job, not just its id");
    }

    private static async Task<IReadOnlyList<string>> SeedDueJobsAsync(CronStoreTestContext context, int count)
    {
        var ids = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var id = $"job-{i}";
            var job = CronStoreTestContext.CreateJob(id, actionType: "test-action") with
            {
                NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            await context.Store.CreateAsync(job);
            ids.Add(id);
        }

        return ids;
    }

    private static CronScheduler CreateScheduler(ICronStore store, ICronAction action, ILogger<CronScheduler> logger)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        return new CronScheduler(
            store,
            [action],
            scopeFactory,
            new StaticOptionsMonitor<CronOptions>(new CronOptions
            {
                Enabled = true,
                TickIntervalSeconds = 1,
                MaxConcurrentJobs = 4
            }),
            logger);
    }

    private static async Task InvokeProcessTickAsync(CronScheduler scheduler)
    {
        var method = typeof(CronScheduler).GetMethod("ProcessTickAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull();
        var task = method!.Invoke(scheduler, [CancellationToken.None]) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (_messages) { return _messages.ToList(); } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_messages) { _messages.Add(formatter(state, exception)); }
        }
    }

    private sealed class CountingAction(string actionType) : ICronAction
    {
        private readonly List<string> _executed = [];

        public string ActionType => actionType;

        public IReadOnlyList<string> ExecutedJobIds
        {
            get { lock (_executed) { return _executed.ToList(); } }
        }

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            lock (_executed) { _executed.Add(context.Job.Id.Value); }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Throws the exact fault #3659 observed live - <c>SQLITE_BUSY</c> escaping
    /// <c>RecordRunStartAsync</c> after the busy_timeout is exhausted - for exactly one job, and
    /// passes every other call through to the real store.
    /// </summary>
    private sealed class FaultInjectingCronStore(ICronStore inner, JobId failFor) : ICronStore
    {
        private int _faults;

        public int Faults => Volatile.Read(ref _faults);

        public Task<CronRun> RecordRunStartAsync(JobId jobId, CancellationToken ct = default)
        {
            if (jobId == failFor)
            {
                Interlocked.Increment(ref _faults);
                throw new SqliteException("SQLite Error 5: 'database is locked'.", 5);
            }

            return inner.RecordRunStartAsync(jobId, ct);
        }

        public Task InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);

        public Task<CronJob> CreateAsync(CronJob job, CancellationToken ct = default) => inner.CreateAsync(job, ct);

        public Task<CronJob?> GetAsync(JobId jobId, CancellationToken ct = default) => inner.GetAsync(jobId, ct);

        public Task<IReadOnlyList<CronJob>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
            => inner.ListAsync(agentId, ct);

        public Task<CronJob?> UpdateDefinitionAsync(
            CronJob job,
            CronJobOwnershipExpectation? expectedOwnership = null,
            CancellationToken ct = default)
            => inner.UpdateDefinitionAsync(job, expectedOwnership, ct);

        public Task SetNextRunAtAsync(JobId jobId, DateTimeOffset? nextRunAt, CancellationToken ct = default)
            => inner.SetNextRunAtAsync(jobId, nextRunAt, ct);

        public Task SetBackoffUntilAsync(JobId jobId, DateTimeOffset? backoffUntil, CancellationToken ct = default)
            => inner.SetBackoffUntilAsync(jobId, backoffUntil, ct);

        public Task DeleteAsync(JobId jobId, CancellationToken ct = default) => inner.DeleteAsync(jobId, ct);

        public Task RecordRunFinalizationAsync(JobId jobId, DateTimeOffset lastRunAt, string lastRunStatus, string? lastRunError, CancellationToken ct = default)
            => inner.RecordRunFinalizationAsync(jobId, lastRunAt, lastRunStatus, lastRunError, ct);

        public Task RecordRunCompleteAsync(RunId runId, string status, string? error = null, SessionId? sessionId = null, CronRunCost? cost = null, CancellationToken ct = default)
            => inner.RecordRunCompleteAsync(runId, status, error, sessionId, cost, ct);

        public Task<IReadOnlyList<CronJobCostRollup>> GetJobCostRollupsAsync(IReadOnlyCollection<JobId> jobIds, int windowDays = 7, CancellationToken ct = default)
            => inner.GetJobCostRollupsAsync(jobIds, windowDays, ct);

        public Task<IReadOnlyList<CronRun>> GetRunHistoryAsync(JobId jobId, int limit = 20, CancellationToken ct = default)
            => inner.GetRunHistoryAsync(jobId, limit, ct);

        public Task<IReadOnlyList<CronRun>> GetRecentRunsAsync(IReadOnlyCollection<JobId> jobIds, IReadOnlyCollection<string>? statuses = null, int limit = 20, CancellationToken ct = default)
            => inner.GetRecentRunsAsync(jobIds, statuses, limit, ct);

        public Task<ConversationId?> TrySetConversationIdAsync(JobId jobId, ConversationId conversationId, CancellationToken ct = default)
            => inner.TrySetConversationIdAsync(jobId, conversationId, ct);

        public Task<int> PurgeRunsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
            => inner.PurgeRunsOlderThanAsync(cutoff, ct);

        public Task<IReadOnlyList<CronRun>> ListRunningRunsAsync(CancellationToken ct = default)
            => inner.ListRunningRunsAsync(ct);

        public Task<bool> TryRecordMissedRunAsync(JobId jobId, DateTimeOffset scheduledOccurrenceUtc, CancellationToken ct = default)
            => inner.TryRecordMissedRunAsync(jobId, scheduledOccurrenceUtc, ct);
    }
}

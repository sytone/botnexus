using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Cron.Tools;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #3573: cron job ownership must hold at the moment of COMMIT, not merely at the moment of read.
/// </summary>
/// <remarks>
/// <para>
/// <c>CronTool.UpdateAsync</c> reads the job, runs <c>EnsureCanManage</c> against that snapshot,
/// then spends argument parsing, model preflight and an awaited alert-target validation before it
/// writes - and the write rewrites <c>created_by</c>/<c>agent_id</c>, the very columns the decision
/// rested on. These tests drive the interleaving directly at the store seam, which is where the
/// guard must live: the REST controller reaches the same method and a tool-only re-read would
/// leave that seam open.
/// </para>
/// <para>
/// Deliberately NOT thread-race tests. A racing-tasks test would be non-deterministic about
/// whether the window was even entered; sequencing the two writers by hand reproduces the exact
/// hazardous interleaving on every run.
/// </para>
/// </remarks>
public sealed class CronOwnershipCommitRevalidationTests
{
    // AC2 + AC4: writer A reads and is authorized, writer B transfers ownership, writer A commits.
    [Fact]
    public async Task DefinitionUpdate_AfterOwnershipTransferred_IsRejectedAndLeavesOwnershipIntact()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var jobId = JobId.From("job-1");
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));

        // Writer A reads. Its authorization decision is bound to THIS ownership snapshot.
        var readByA = await context.Store.GetAsync(jobId);
        readByA.ShouldNotBeNull();
        var expectation = CronJobOwnershipExpectation.From(readByA!);

        // Writer B transfers ownership while A is still parsing arguments / awaiting preflight.
        var transferred = await context.Store.UpdateDefinitionAsync(
            readByA! with { CreatedBy = "agent-owner", AgentId = AgentId.From("agent-owner") },
            CronJobOwnershipExpectation.From(readByA!));
        transferred.ShouldNotBeNull();

        // Writer A now commits, carrying the ownership capture it was authorized against.
        var capture = readByA! with
        {
            Name = "Captured By A",
            CreatedBy = "agent-attacker",
            AgentId = AgentId.From("agent-attacker")
        };

        await Should.ThrowAsync<CronJobOwnershipChangedException>(
            async () => await context.Store.UpdateDefinitionAsync(capture, expectation));

        // AC4: the ownership columns - and the rest of the definition - are untouched.
        var stored = await context.Store.GetAsync(jobId);
        stored.ShouldNotBeNull();
        stored!.CreatedBy.ShouldBe("agent-owner");
        stored.AgentId!.Value.Value.ShouldBe("agent-owner");
        stored.Name.ShouldNotBe("Captured By A");
    }

    // AC3: the rejection reaches the model-facing tool as UnauthorizedAccessException - the same
    // type EnsureCanManage throws for the read-time refusal - and never as KeyNotFoundException.
    [Fact]
    public async Task CronTool_Update_WhenOwnershipChangesMidFlight_ThrowsUnauthorizedNotNotFound()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var jobId = JobId.From("job-1");
        await context.Store.CreateAsync(
            CronStoreTestContext.CreateJob("job-1") with { CreatedBy = "agent-a", AgentId = AgentId.From("agent-a") });

        var store = new OwnershipRacingCronStore(
            context.Store,
            onReadForUpdate: async inner =>
            {
                var current = await inner.GetAsync(jobId);
                await inner.UpdateDefinitionAsync(
                    current! with { CreatedBy = "agent-b", AgentId = AgentId.From("agent-b") },
                    CronJobOwnershipExpectation.From(current!));
            });

        var tool = new CronTool(store, CreateScheduler(), AgentId.From("agent-a"));
        var arguments = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-1",
            ["name"] = "Renamed by the stale owner"
        });

        var thrown = await Should.ThrowAsync<UnauthorizedAccessException>(
            async () => await tool.ExecuteAsync("call-1", arguments));
        thrown.ShouldBeOfType<CronJobOwnershipChangedException>();

        var stored = await context.Store.GetAsync(jobId);
        stored!.CreatedBy.ShouldBe("agent-b");
        stored.AgentId!.Value.Value.ShouldBe("agent-b");
        stored.Name.ShouldNotBe("Renamed by the stale owner");
    }

    // The guard must not fire on the ordinary path: an edit whose expectation still matches commits.
    [Fact]
    public async Task DefinitionUpdate_WithMatchingExpectation_Commits()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var jobId = JobId.From("job-1");
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));

        var read = await context.Store.GetAsync(jobId);
        var saved = await context.Store.UpdateDefinitionAsync(
            read! with { Name = "Ordinary edit" },
            CronJobOwnershipExpectation.From(read!));

        saved.ShouldNotBeNull();
        saved!.Name.ShouldBe("Ordinary edit");
    }

    // Null-safety: a job with a NULL agent_id must still be matchable. `=` would never match NULL,
    // silently rejecting every edit of an untargeted job; SQLite's `IS` is the null-safe form.
    [Fact]
    public async Task DefinitionUpdate_WithNullAgentIdExpectation_Commits()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var jobId = JobId.From("job-null-agent");
        await context.Store.CreateAsync(
            CronStoreTestContext.CreateJob("job-null-agent") with { AgentId = null });

        var read = await context.Store.GetAsync(jobId);
        read.ShouldNotBeNull();
        read!.AgentId.ShouldBeNull();

        var saved = await context.Store.UpdateDefinitionAsync(
            read with { Name = "Edited untargeted job" },
            CronJobOwnershipExpectation.From(read));

        saved.ShouldNotBeNull();
        saved!.Name.ShouldBe("Edited untargeted job");
    }

    // A caller that passes no expectation keeps the pre-#3573 behaviour. This is the provisioner /
    // scheduler path, which is not acting on behalf of an agent and has no stale decision to guard.
    [Fact]
    public async Task DefinitionUpdate_WithoutExpectation_IsUnguarded()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var jobId = JobId.From("job-1");
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-1"));

        var read = await context.Store.GetAsync(jobId);
        var saved = await context.Store.UpdateDefinitionAsync(
            read! with { Name = "System edit", CreatedBy = "system" });

        saved.ShouldNotBeNull();
        saved!.CreatedBy.ShouldBe("system");
    }

    // A missing job is still absence, not an ownership failure - the two answers must not collapse.
    [Fact]
    public async Task DefinitionUpdate_OnMissingJob_ReturnsNullRatherThanThrowing()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var ghost = CronStoreTestContext.CreateJob("no-such-job");

        var saved = await context.Store.UpdateDefinitionAsync(
            ghost,
            CronJobOwnershipExpectation.From(ghost));

        saved.ShouldBeNull();
    }

    private static CronScheduler CreateScheduler()
    {
        var scopeFactory = new ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        return new CronScheduler(
            new Mock<ICronStore>().Object,
            Array.Empty<ICronAction>(),
            scopeFactory,
            new StaticOptionsMonitor(new CronOptions()),
            NullLogger<CronScheduler>.Instance);
    }

    private sealed class StaticOptionsMonitor(CronOptions currentValue) : Microsoft.Extensions.Options.IOptionsMonitor<CronOptions>
    {
        public CronOptions CurrentValue { get; } = currentValue;
        public CronOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<CronOptions, string?> listener) => null;
    }

    /// <summary>
    /// Decorator that runs a side effect the first time the job is read, reproducing an ownership
    /// transfer landing inside the tool's read-authorize-write window without any timing luck.
    /// </summary>
    private sealed class OwnershipRacingCronStore(ICronStore inner, Func<ICronStore, Task> onReadForUpdate) : ICronStore
    {
        private int _reads;

        public Task InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);

        public Task<CronJob> CreateAsync(CronJob job, CancellationToken ct = default) => inner.CreateAsync(job, ct);

        public async Task<CronJob?> GetAsync(JobId jobId, CancellationToken ct = default)
        {
            var job = await inner.GetAsync(jobId, ct);
            if (Interlocked.Increment(ref _reads) == 1)
                await onReadForUpdate(inner);
            return job;
        }

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

        public Task<CronRun> RecordRunStartAsync(JobId jobId, CancellationToken ct = default)
            => inner.RecordRunStartAsync(jobId, ct);

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

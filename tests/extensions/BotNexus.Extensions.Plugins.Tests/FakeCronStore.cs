using BotNexus.Cron;
using BotNexus.Domain.Primitives;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// An in-memory <see cref="ICronStore"/> covering exactly the surface the plugin-update
/// provisioner touches: initialise, get, create, and definition update.
/// </summary>
/// <remarks>
/// The unimplemented members throw rather than returning a benign empty value on purpose. A
/// provisioner that started calling <c>DeleteAsync</c> or <c>ListAsync</c> would be doing
/// something the idempotency clause forbids, and a double that quietly absorbed such a call
/// would let that regression pass. Failing loudly turns an unexpected store interaction into a
/// test failure instead of a silent behaviour change.
/// </remarks>
internal sealed class FakeCronStore : ICronStore
{
    private readonly Dictionary<string, CronJob> _jobs = new(StringComparer.Ordinal);

    /// <summary>Number of times <see cref="CreateAsync"/> was called, for idempotency assertions.</summary>
    public int CreateCallCount { get; private set; }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<CronJob> CreateAsync(CronJob job, CancellationToken ct = default)
    {
        CreateCallCount++;
        _jobs[job.Id.Value] = job;
        return Task.FromResult(job);
    }

    public Task<CronJob?> GetAsync(JobId jobId, CancellationToken ct = default) =>
        Task.FromResult(_jobs.TryGetValue(jobId.Value, out var job) ? job : null);

    public Task<CronJob?> UpdateDefinitionAsync(
        CronJob job,
        CronJobOwnershipExpectation? expectedOwnership = null,
        CancellationToken ct = default)
    {
        _jobs[job.Id.Value] = job;
        return Task.FromResult<CronJob?>(job);
    }

    public Task<IReadOnlyList<CronJob>> ListAsync(AgentId? agentId = null, CancellationToken ct = default) =>
        throw new NotSupportedException("The plugin-update provisioner must not enumerate jobs.");

    public Task SetBackoffUntilAsync(JobId jobId, DateTimeOffset? backoffUntil, CancellationToken ct = default) =>
        throw new NotSupportedException("The plugin-update provisioner must not set a backoff floor.");

    public Task SetNextRunAtAsync(JobId jobId, DateTimeOffset? nextRunAt, CancellationToken ct = default) =>
        throw new NotSupportedException("The plugin-update provisioner must not schedule runs.");

    public Task RecordRunFinalizationAsync(JobId jobId, DateTimeOffset lastRunAt, string lastRunStatus, string? lastRunError, CancellationToken ct = default) =>
        throw new NotSupportedException("The plugin-update provisioner must not finalise runs.");

    public Task DeleteAsync(JobId jobId, CancellationToken ct = default) =>
        throw new NotSupportedException("The plugin-update provisioner must never delete a job.");

    public Task<CronRun> RecordRunStartAsync(JobId jobId, CancellationToken ct = default) =>
        throw new NotSupportedException("The plugin-update provisioner must not start runs.");

    public Task RecordRunCompleteAsync(RunId runId, string status, string? error = null, SessionId? sessionId = null, CronRunCost? cost = null, CancellationToken ct = default) =>
        throw new NotSupportedException("The plugin-update provisioner must not complete runs.");

    public Task<IReadOnlyList<CronJobCostRollup>> GetJobCostRollupsAsync(IReadOnlyCollection<JobId> jobIds, int windowDays, CancellationToken ct = default) =>
        throw new NotSupportedException("The plugin-update provisioner must not read cost rollups.");

    public Task<IReadOnlyList<CronRun>> GetRunHistoryAsync(JobId jobId, int limit = 20, CancellationToken ct = default) =>
        throw new NotSupportedException("The plugin-update provisioner must not read run history.");

    public Task<IReadOnlyList<CronRun>> GetRecentRunsAsync(IReadOnlyCollection<JobId> jobIds, IReadOnlyCollection<string>? statuses = null, int limit = 20, CancellationToken ct = default) =>
        throw new NotSupportedException("The plugin-update provisioner must not read recent runs.");

    public Task<ConversationId?> TrySetConversationIdAsync(JobId jobId, ConversationId conversationId, CancellationToken ct = default) =>
        throw new NotSupportedException("An agentless plugin-update job has no conversation to stamp.");

    public Task<int> PurgeRunsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
        throw new NotSupportedException("The plugin-update provisioner must not purge runs.");

    public Task<IReadOnlyList<CronRun>> ListRunningRunsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("The plugin-update provisioner must not reap runs.");

    public Task<bool> TryRecordMissedRunAsync(JobId jobId, DateTimeOffset scheduledOccurrenceUtc, CancellationToken ct = default) =>
        throw new NotSupportedException("The plugin-update provisioner must not record missed runs.");
}

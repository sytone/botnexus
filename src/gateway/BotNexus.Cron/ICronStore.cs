using BotNexus.Domain.Primitives;

namespace BotNexus.Cron;

public interface ICronStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<CronJob> CreateAsync(CronJob job, CancellationToken ct = default);
    Task<CronJob?> GetAsync(JobId jobId, CancellationToken ct = default);
    Task<IReadOnlyList<CronJob>> ListAsync(AgentId? agentId = null, CancellationToken ct = default);
    Task<CronJob> UpdateAsync(CronJob job, CancellationToken ct = default);
    Task DeleteAsync(JobId jobId, CancellationToken ct = default);
    Task<CronRun> RecordRunStartAsync(JobId jobId, CancellationToken ct = default);
    Task RecordRunCompleteAsync(RunId runId, string status, string? error = null, SessionId? sessionId = null, CancellationToken ct = default);
    Task<IReadOnlyList<CronRun>> GetRunHistoryAsync(JobId jobId, int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// Atomically stamps <paramref name="conversationId"/> onto a job whose
    /// <c>ConversationId</c> is currently <c>null</c>. Returns the winning conversation
    /// id (which may differ from <paramref name="conversationId"/> if a concurrent run
    /// won the race). Returns <c>null</c> if the job no longer exists.
    /// </summary>
    /// <remarks>
    /// CAS primitive used by <see cref="CronScheduler"/> to make first-run conversation
    /// reservation race-safe. The CronJob.ConversationId field is the canonical link from
    /// a cron job to its conversation under P9-D — this CAS guarantees only one stamp wins.
    /// </remarks>
    Task<ConversationId?> TrySetConversationIdAsync(JobId jobId, ConversationId conversationId, CancellationToken ct = default);

    /// <summary>
    /// Purges terminal cron run records older than <paramref name="cutoff"/>. A run is
    /// terminal when its status is one of the scheduler-written outcomes
    /// <see cref="CronRunStatus.Ok"/>, <see cref="CronRunStatus.Error"/>, or
    /// <see cref="CronRunStatus.TimedOut"/> and its completed_at timestamp is earlier than
    /// the cutoff. In-flight runs (<see cref="CronRunStatus.Running"/>) are never deleted,
    /// regardless of age, so in-progress work is preserved. Returns the number of rows deleted.
    /// </summary>
    Task<int> PurgeRunsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

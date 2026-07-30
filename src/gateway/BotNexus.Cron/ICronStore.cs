using BotNexus.Domain.Primitives;

namespace BotNexus.Cron;

public interface ICronStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<CronJob> CreateAsync(CronJob job, CancellationToken ct = default);
    Task<CronJob?> GetAsync(JobId jobId, CancellationToken ct = default);
    Task<IReadOnlyList<CronJob>> ListAsync(AgentId? agentId = null, CancellationToken ct = default);
    /// <summary>
    /// Applies a user-owned job <b>definition</b> update. Writes only the caller-authored
    /// columns; it must not touch scheduler-owned runtime bookkeeping (<c>LastRun*</c>,
    /// <c>NextRunAt</c>) or the CAS-established <c>ConversationId</c>. This keeps a
    /// controller/tool edit from regressing a concurrent run's status, timestamps, next run,
    /// or conversation pin (#2133). Rescheduling after a schedule change is a separate
    /// <see cref="SetNextRunAtAsync"/> call. Returns the re-read job, or <c>null</c> if the
    /// job no longer exists.
    /// </summary>
    Task<CronJob?> UpdateDefinitionAsync(CronJob job, CancellationToken ct = default);

    /// <summary>
    /// Scheduler-owned narrow write of <c>NextRunAt</c> only. Used for initialization,
    /// stale-schedule correction, post-run rescheduling, and the reschedule half of a
    /// schedule-changing definition edit. Never touches definition columns, <c>LastRun*</c>,
    /// or the conversation pin, so it cannot clobber a concurrent definition edit (#2133).
    /// </summary>
    Task SetNextRunAtAsync(JobId jobId, DateTimeOffset? nextRunAt, CancellationToken ct = default);

    /// <summary>
    /// Scheduler-owned narrow write of terminal run bookkeeping (<c>LastRunAt</c>,
    /// <c>LastRunStatus</c>, <c>LastRunError</c>) for a completed run. Never touches
    /// definition columns, <c>NextRunAt</c>, or the conversation pin, so run finalization
    /// racing a concurrent definition edit cannot overwrite it (#2133).
    /// </summary>
    Task RecordRunFinalizationAsync(JobId jobId, DateTimeOffset lastRunAt, string lastRunStatus, string? lastRunError, CancellationToken ct = default);
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

    /// <summary>
    /// Lists every run still stamped <see cref="CronRunStatus.Running"/>, newest first. Used by the
    /// scheduler's orphaned-run reaper (#2410): a run whose owning process died without a terminal
    /// write stays <c>running</c> forever, which also makes it permanently immune to
    /// <see cref="PurgeRunsOlderThanAsync"/>. Implementers must return non-terminal rows only -
    /// the caller decides which of them are too old (or too far in the future) to be genuine.
    /// </summary>
    Task<IReadOnlyList<CronRun>> ListRunningRunsAsync(CancellationToken ct = default);

    /// <summary>
    /// Idempotently records a single <c>missed</c> run for the scheduled occurrence
    /// <paramref name="scheduledOccurrenceUtc"/>. Returns <c>true</c> when a new history row was
    /// written and <c>false</c> when an equivalent row already existed.
    /// </summary>
    /// <remarks>
    /// Startup missed-run detection re-scans the window between a job's last real execution and
    /// now on every gateway start. Because the missed-run path deliberately does not advance the
    /// job's <c>last_run_at</c> (no execution occurred), the same window is rescanned after every
    /// restart. Implementers must therefore key the row on <c>(jobId, scheduledOccurrenceUtc)</c>
    /// so repeated scans converge instead of accumulating duplicate history (#2477). The row is
    /// written already-terminal: <c>started_at</c> carries the scheduled occurrence rather than
    /// the scan wall-clock, and scheduler-owned job bookkeeping (<c>last_run_*</c>) is untouched
    /// because no execution actually happened.
    /// </remarks>
    Task<bool> TryRecordMissedRunAsync(JobId jobId, DateTimeOffset scheduledOccurrenceUtc, CancellationToken ct = default);
}

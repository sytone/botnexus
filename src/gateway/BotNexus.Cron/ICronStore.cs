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
    /// <param name="job">The definition to write.</param>
    /// <param name="expectedOwnership">
    /// The ownership state the CALLER's authorization decision was made against (#3573). When
    /// supplied, implementers must make the write conditional on the stored <c>created_by</c> and
    /// <c>agent_id</c> still matching it, and throw <see cref="CronJobOwnershipChangedException"/>
    /// when they do not - rather than committing an update authorized against an owner who has
    /// since been replaced. Comparison must be NULL-SAFE in both columns, or every edit of an
    /// untargeted job would be rejected.
    /// <para>
    /// Passing <c>null</c> keeps the pre-#3573 unconditional write and is correct only for callers
    /// that are not acting on behalf of an agent and therefore hold no stale authorization
    /// decision - the scheduler's own reconciliation and the system-job provisioners.
    /// </para>
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<CronJob?> UpdateDefinitionAsync(
        CronJob job,
        CronJobOwnershipExpectation? expectedOwnership = null,
        CancellationToken ct = default);

    /// <summary>
    /// Scheduler-owned narrow write of <c>NextRunAt</c> only. Used for initialization,
    /// stale-schedule correction, post-run rescheduling, and the reschedule half of a
    /// schedule-changing definition edit. Never touches definition columns, <c>LastRun*</c>,
    /// or the conversation pin, so it cannot clobber a concurrent definition edit (#2133).
    /// </summary>
    Task SetNextRunAtAsync(JobId jobId, DateTimeOffset? nextRunAt, CancellationToken ct = default);

    /// <summary>
    /// Scheduler-owned narrow write of <c>BackoffUntil</c> only (#3350): the job-authored floor
    /// before which the job has asked not to be woken. Passing <c>null</c> clears the floor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately a SEPARATE method rather than an extra parameter on
    /// <see cref="SetNextRunAtAsync"/>. The whole point of #3350 is that the expression cache and
    /// the job-authored floor are two different facts with two different owners; a single write
    /// that could set both would re-entangle them at the very seam introduced to separate them,
    /// and would let a routine reschedule silently cancel a backoff.
    /// </para>
    /// <para>
    /// Like <see cref="SetNextRunAtAsync"/> it touches no definition column, no <c>LastRun*</c>
    /// bookkeeping and not the conversation pin, so it cannot clobber a concurrent definition
    /// edit (#2133) - and symmetrically, a definition update must never write this column.
    /// </para>
    /// </remarks>
    Task SetBackoffUntilAsync(JobId jobId, DateTimeOffset? backoffUntil, CancellationToken ct = default);

    /// <summary>
    /// Scheduler-owned narrow write of terminal run bookkeeping (<c>LastRunAt</c>,
    /// <c>LastRunStatus</c>, <c>LastRunError</c>) for a completed run. Never touches
    /// definition columns, <c>NextRunAt</c>, or the conversation pin, so run finalization
    /// racing a concurrent definition edit cannot overwrite it (#2133).
    /// </summary>
    Task RecordRunFinalizationAsync(JobId jobId, DateTimeOffset lastRunAt, string lastRunStatus, string? lastRunError, CancellationToken ct = default);
    Task DeleteAsync(JobId jobId, CancellationToken ct = default);
    Task<CronRun> RecordRunStartAsync(JobId jobId, CancellationToken ct = default);
    /// <summary>
    /// Records a run's terminal outcome and, when supplied, its per-run cost measurements (#2641).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cost write rides THIS method deliberately: it is the single path every terminal outcome
    /// already flows through - <c>ok</c>, <c>error</c>, <c>timed_out</c>, <c>no_tool_calls</c>,
    /// <c>delivery_failed</c> and <c>aborted</c> alike - so a failed run still records the cost of
    /// the work it did before it failed. Adding a separate cost hook would have re-created the
    /// only-the-happy-path defect that #3161 and #2985 each had to fix at this same seam.
    /// </para>
    /// <para>
    /// <paramref name="cost"/> being null, or carrying only nulls, must leave the stored columns
    /// untouched rather than overwrite them with zeros: null means "not measured".
    /// </para>
    /// </remarks>
    Task RecordRunCompleteAsync(RunId runId, string status, string? error = null, SessionId? sessionId = null, CronRunCost? cost = null, CancellationToken ct = default);

    /// <summary>
    /// Derives per-job cost rollups from run history over the last <paramref name="windowDays"/>
    /// days (#2641), one entry per job in <paramref name="jobIds"/> that has at least one run in
    /// the window, ordered by total token spend descending (unmeasured jobs last).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window is clamped to <c>CronRunRetentionOptions.RetentionDays</c> by the implementation,
    /// and the clamp is reported on each rollup via
    /// <see cref="CronJobCostRollup.WindowTruncatedByRetention"/>. An unclamped longer window would
    /// report a total missing every purged run while being indistinguishable from a complete one.
    /// </para>
    /// <para>
    /// An EMPTY <paramref name="jobIds"/> means "no jobs" and returns nothing - never "no filter",
    /// the same scoping rule <see cref="GetRecentRunsAsync"/> established in #2838.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<CronJobCostRollup>> GetJobCostRollupsAsync(
        IReadOnlyCollection<JobId> jobIds,
        int windowDays,
        CancellationToken ct = default);
    Task<IReadOnlyList<CronRun>> GetRunHistoryAsync(JobId jobId, int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// Cross-job recent-run query (#2838). Returns the newest runs across <paramref name="jobIds"/>,
    /// optionally narrowed to <paramref name="statuses"/>, newest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every pre-#2838 run query was keyed on a single job id, so "which of my jobs have failed
    /// recently" cost one call per job and was only ever asked after a human noticed something
    /// missing. The #2819 hijack therefore ran for ~2 days across at least 4 jobs with no signal.
    /// </para>
    /// <para>
    /// The scope is passed in as an explicit job-id set rather than an agent id: authorisation is
    /// the caller's to decide (the tool applies the same <c>EnsureCanManage</c> rule as the per-job
    /// path), and the store must not carry a second, subtly different notion of ownership.
    /// An EMPTY <paramref name="jobIds"/> means "no jobs" and must return nothing - never
    /// "no filter", which is how a scoped query silently becomes a global one.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<CronRun>> GetRecentRunsAsync(
        IReadOnlyCollection<JobId> jobIds,
        IReadOnlyCollection<string>? statuses = null,
        int limit = 20,
        CancellationToken ct = default);

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

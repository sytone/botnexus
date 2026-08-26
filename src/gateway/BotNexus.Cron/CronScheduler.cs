using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron;

public sealed class CronScheduler(
    ICronStore cronStore,
    IEnumerable<ICronAction> actions,
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<CronOptions> optionsMonitor,
    ILogger<CronScheduler> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly ICronStore _cronStore = cronStore;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IOptionsMonitor<CronOptions> _optionsMonitor = optionsMonitor;
    private readonly ILogger<CronScheduler> _logger = logger;

    // #2634: the lifecycle checks (notably expiry) must be assertable without wall-clock waits, so
    // the scheduler reads "now" through an injectable TimeProvider. Optional and defaulting to the
    // system clock, so every existing registration and call site is unaffected.
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IReadOnlyDictionary<string, ICronAction> _actions = actions
        .GroupBy(action => action.ActionType, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

    // Per-job in-process lock guards the "create conversation -> CAS stamp" critical section so
    // concurrent runs of the SAME job in this process cannot both create their own conversation.
    // Multi-process races (e.g. CLI `cron run` while the gateway scheduler also fires) are still
    // possible but are cleaned up by the next scheduler-startup migration sweep.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _jobLocks = new(StringComparer.Ordinal);

    // #3160: THE registry of runs currently in flight in this process, keyed by RUN id.
    //
    // _jobLocks above looks like the right shape for this and is not: it is a serialisation mutex
    // holding no cancellation handle whatsoever, which is precisely why a delete had nothing to
    // signal. Keying by run id rather than job id is deliberate - a job can legitimately have more
    // than one run registered at once (a second trigger parks on the per-job lock while the first
    // executes), and a job-keyed map would silently drop one of them, leaving an uncancellable run.
    // Cancelling a job therefore fans out over its runs by an EXACT ordinal job-id match; a prefix
    // or loose comparison here is how deleting `job-1` would kill `job-10`.
    private readonly ConcurrentDictionary<string, ActiveCronRun> _activeRuns = new(StringComparer.Ordinal);

    /// <summary>
    /// #3517: how many consecutive one-shot deletion failures a job is allowed before it is driven
    /// to a terminal state instead of being re-armed for another attempt.
    /// </summary>
    /// <remarks>
    /// Three, not one: a single transient store or archive hiccup should not strand a job that would
    /// have cleaned itself up on the next tick. What must not survive is the UNBOUNDED case - the
    /// reported incident retried the identical failure 154 times over 15 hours with no decay.
    /// </remarks>
    internal const int MaxOneShotDeleteAttempts = 3;

    // #3517: consecutive one-shot deletion failures per job id. Process-scoped and deliberately not
    // persisted - the bound exists to stop a wedged run's job hammering the error channel for the
    // life of THIS process, and a gateway restart clears the wedged run along with the counter, so
    // a fresh attempt after a restart is the correct behaviour rather than a leak.
    private readonly ConcurrentDictionary<string, int> _oneShotDeleteFailures = new(StringComparer.Ordinal);

    /// <summary>
    /// Number of runs currently registered as in flight in this process (#3160). Exposed so the
    /// registry's lifecycle is assertable as an observable on every terminal path rather than
    /// inferred from a log line - a leaked entry is both a memory leak and a stale cancellation
    /// handle pointed at a run that has already finished.
    /// </summary>
    internal int ActiveRunCount => _activeRuns.Count;

    /// <summary>
    /// A single in-flight cron run: the source that cancels it and the signal that says the run has
    /// actually <b>observed</b> that cancellation and left its action body (#3160 AC6).
    /// </summary>
    /// <remarks>
    /// The observation signal is what makes a delete safe to proceed from. Cancelling and
    /// immediately archiving the conversation / sweeping the run's sessions would race a run that
    /// is still mid-write, which is the concrete corruption #3160 reports.
    /// </remarks>
    private sealed class ActiveCronRun(JobId jobId, CancellationTokenSource cts)
    {
        public JobId Job { get; } = jobId;
        public CancellationTokenSource Cts { get; } = cts;

        /// <summary>Completes when the run has left <c>RunActionAsync</c>'s body, however it ended.</summary>
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Set when this run was cancelled by an operator delete/disable rather than by the host
        /// token or its own timeout, so the terminal-status mapping can tell the three apart.
        /// </summary>
        public bool OperatorCancelled { get; private set; }

        public void RequestOperatorCancel()
        {
            OperatorCancelled = true;
            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run reached its terminal path and disposed the source between our lookup and
                // this call. There is nothing left to cancel, and racing a natural completion is
                // not an error.
            }
        }
    }

    // One-shot legacy migration guard. The scheduler runs the legacy-conversation migration
    // exactly once per process lifetime, gated by this flag.
    private int _migrationRan;

    /// <summary>
    /// Distinct terminal reason stamped on runs reaped by <see cref="ReapOrphanedRunsAsync"/>, so an
    /// orphaned run is never confused with a genuine action failure or a graceful abort (#2410).
    /// </summary>
    internal const string OrphanedRunReason = "Cron run orphaned - no terminal write was recorded by its owning process.";

    public async Task<CronRun> RunNowAsync(JobId jobId, CancellationToken cancellationToken = default)
    {
        await _cronStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var job = await _cronStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Cron job '{jobId}' was not found.");

        return await RunActionAsync(job, CronTriggerType.Manual, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a cron job, archives its associated conversation, and reclaims the
    /// <c>cron:</c>-scoped run sessions the job owns.
    /// Per directive G-5, the conversation lives "until deleted" — deleting the job is
    /// the canonical signal to archive the conversation thread.
    /// </summary>
    /// <remarks>
    /// Idempotent: missing jobs and missing conversations are not errors. #3160: any run this job
    /// has in flight is cancelled FIRST and awaited (bounded) before anything is torn down, so the
    /// archive and the session sweep cannot race a run that is still writing. The conversation
    /// is archived next (best-effort) before the job row is removed, so a failure to
    /// archive surfaces an error and leaves the job intact for retry. Session cleanup (#2893)
    /// runs next and is best-effort in the opposite direction - a session-store failure is logged
    /// and never aborts the delete.
    /// #3517: the archive is SKIPPED (with a warning) when cancellation was not actually observed,
    /// because a still-live run holds the conversation's write stripe and the archive is then
    /// guaranteed to fail. Aborting the delete on that guaranteed failure is what produced the
    /// unbounded retry loop.
    /// </remarks>
    public async Task DeleteJobAsync(JobId jobId, CancellationToken cancellationToken = default)
    {
        await _cronStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var existing = await _cronStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            _logger.LogDebug("DeleteJobAsync: job '{JobId}' was not found; nothing to delete.", jobId);
            return;
        }

        // #3160: cancel BEFORE anything is torn down, and wait (bounded) for the run to observe it.
        // Ordering is the whole fix. Archiving the conversation or sweeping the run's sessions while
        // the action is still executing is not merely untidy - the run keeps writing into a
        // conversation that has just been archived (resurrecting it) and the sweep deletes the very
        // session rows the run is mid-write on. Both are states #3160 observed in production.
        var cancellation = await CancelActiveRunsAsync(jobId, cancellationToken).ConfigureAwait(false);

        // #3517: the #3160 ordering only holds when the cancellation was actually OBSERVED. When it
        // was not, the run is still live and still holding this conversation's write stripe, so the
        // archive below is not merely risky - it is guaranteed to fail, and its failure used to
        // abort the whole delete and re-arm an unbounded retry. Skip it explicitly and say so.
        // The delete itself still proceeds: fail-open, exactly as the cancellation watchdog does.
        if (existing.ConversationId.HasValue && !cancellation.Observed)
        {
            _logger.LogWarning(
                "Not archiving conversation '{ConversationId}' for cron job '{JobId}': {Count} in-flight run(s) never "
                + "observed cancellation, so the conversation is still being written to. The job is being deleted anyway; "
                + "the conversation is left active rather than blocking the delete on a step that cannot succeed.",
                existing.ConversationId.Value,
                jobId,
                cancellation.Signalled);
        }
        else if (existing.ConversationId.HasValue)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
                await conversations.ArchiveAsync(existing.ConversationId.Value, "cron-delete-after-run", jobId.Value, "system", cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Archived conversation '{ConversationId}' for deleted cron job '{JobId}'.",
                    existing.ConversationId.Value,
                    jobId);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to archive conversation '{ConversationId}' for cron job '{JobId}'. Aborting delete so the job can be retried.",
                    existing.ConversationId.Value,
                    jobId);
                throw;
            }
        }

        await DeleteOwnedRunSessionsAsync(existing, cancellationToken).ConfigureAwait(false);

        await _cronStore.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Job-lifecycle session cleanup (#2893). Deletes the <c>cron:</c>-scoped run sessions the job
    /// owns, so deleting a job leaves no unreferenced session rows or transcripts behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-run counterpart (<see cref="MaybeDeleteEphemeralRunSessionAsync"/>, #1561) only fires
    /// when the job opted into <see cref="CronJob.DeleteAfterRun"/>, which is off by default. Every
    /// other job therefore stranded one session plus one transcript per historical run at the moment
    /// its owning job row was removed. This closes the job-lifecycle half of that ownership.
    /// </para>
    /// <para>
    /// Eligibility is deliberately narrow and matches the id convention <c>CronTrigger</c> writes,
    /// <c>cron:{jobIdSlug}:{timestamp}:{guid}</c>:
    /// </para>
    /// <list type="bullet">
    ///   <item>Scoped to the job's own agent, so an unrelated agent's sessions are never enumerated.</item>
    ///   <item>Only ids beginning with <c>cron:{jobIdSlug}:</c> - the trailing colon is load-bearing,
    ///   without it deleting <c>job-1</c> would also claim every session of <c>job-10</c>.</item>
    ///   <item>The legacy jobId-less form <c>cron:{timestamp}:{guid}</c> is skipped: it cannot be
    ///   attributed to a job, and a wrong guess destroys another job's transcript.</item>
    /// </list>
    /// <para>
    /// Best-effort by construction: the whole sweep is wrapped so a session-store outage is logged
    /// and swallowed rather than aborting the delete. Leaving the job row behind because reclamation
    /// failed would make the delete permanently unachievable against a broken store, which is a worse
    /// outcome than the leak this method exists to prevent. It runs after the conversation archive
    /// (which does still abort the delete on failure) and before the job row is removed.
    /// </para>
    /// </remarks>
    private async Task DeleteOwnedRunSessionsAsync(CronJob job, CancellationToken cancellationToken)
    {
        if (job.AgentId is not { } agentId)
            return;

        var prefix = $"cron:{Sanitize(job.Id.Value)}:";
        var deleted = 0;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionStore>();
            var owned = await sessions.ListAsync(agentId, cancellationToken).ConfigureAwait(false);

            foreach (var session in owned)
            {
                if (!session.SessionId.Value.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                await sessions.DeleteAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
                deleted++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to delete owned cron run sessions for job '{JobId}' (prefix '{Prefix}'). The job delete continues; any surviving sessions are orphaned.",
                job.Id,
                prefix);
            return;
        }

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Deleted {Count} cron run session(s) (and their transcripts) owned by deleted cron job '{JobId}'.",
                deleted,
                job.Id);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _cronStore.InitializeAsync(stoppingToken).ConfigureAwait(false);
        _logger.LogInformation("Cron scheduler started. Tick interval: {Interval}s", _optionsMonitor.CurrentValue?.TickIntervalSeconds ?? 60);

        // One-shot legacy-conversation migration: rebinds sessions left orphaned by the
        // pre-P9-D composite-id model onto the canonical per-job conversation.
        try
        {
            await MigrateLegacyCronConversationsAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Legacy cron conversation migration failed. Scheduler will continue running.");
        }

        // #2410: startup sweep. Runs left stamped Running by a previous process that died without a
        // terminal write (kill, host crash, OOM, power loss) are invisible AND unprunable; reap them
        // before the first tick so the history reflects reality from the moment the scheduler starts.
        try
        {
            await ReapOrphanedRunsAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Orphaned cron run reaping failed at startup. Scheduler will continue running.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _optionsMonitor.CurrentValue ?? new CronOptions();
            await SyncConfiguredJobsAsync(options, stoppingToken).ConfigureAwait(false);
            if (options.Enabled)
            {
                try
                {
                    await ProcessTickAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Cron scheduler tick failed.");
                }
            }

            var delay = TimeSpan.FromSeconds(Math.Max(1, options.TickIntervalSeconds));
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessTickAsync(CancellationToken ct)
    {
        // #2410: the reaper is periodic as well as startup-bound - a run can be orphaned by a crash
        // of a sibling process while this scheduler keeps ticking. Failures here must never abort the
        // tick, so they are logged and swallowed.
        try
        {
            await ReapOrphanedRunsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Orphaned cron run reaping failed during tick.");
        }

        var jobs = await _cronStore.ListAsync(ct: ct).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();

        // Phase 1 (sequential): resolve NextRunAt for uninitialised or stale jobs.
        // These are cheap store-only operations with no agent I/O.
        var dueJobs = new List<(CronJob Job, CronExpression Expression)>();
        foreach (var job in jobs.Where(j => j.Enabled))
        {
            // #2634 (schedule time): an expired job is dropped from the due scan entirely, so it
            // never even reaches the execute phase. This is the cheap early-out; it is NOT the
            // authoritative gate (see IsExpired's call site in RunActionAsync) because a manual
            // RunNowAsync bypasses this loop and an expiry can elapse between this scan and the
            // actual fire. NextRunAt is deliberately left as-is: expiry suppresses execution, it
            // does not mutate the stored job.
            if (IsExpired(job))
            {
                _logger.LogDebug(
                    "Cron job '{JobId}' is past its expiry ({ExpiresAt:o}); skipping in the due scan.",
                    job.Id,
                    job.ExpiresAt);
                continue;
            }

            if (!TryGetSchedule(job, out var expression))
                continue;

            var tz = CronTimeZoneResolver.Resolve(job.TimeZone, _logger, job.Id);
            // #2810: transition policy lives in CronExpressionExtensions, never inline here.
            var computedNext = expression.NextRun(now, tz);

            if (job.NextRunAt is null)
            {
                // #2133: narrow next_run_at write - never round-trips definition columns,
                // so it cannot clobber a concurrent controller/tool definition edit.
                await _cronStore.SetNextRunAtAsync(job.Id, computedNext, ct).ConfigureAwait(false);
                continue;
            }

            // Detect stale NextRunAt: if the schedule was changed to fire sooner than the stored
            // value, correct it so the job isn't stuck waiting on a NextRunAt that no longer
            // matches the current schedule.
            //
            // #3350: this correction is sound ONLY under the reading "NextRunAt is the expression's
            // next occurrence, cached". It is unsound under the other reading the field used to
            // carry - "the time this job asked to be woken" - because a job that deliberately backs
            // off writes a LATER value on purpose, and "computed is sooner than stored" cannot tell
            // a deliberate deferral from a stale cache. The two meanings now live in two fields:
            // NextRunAt is the cache and is corrected freely here; BackoffUntil is the job-authored
            // floor and is never moved forward by the scheduler.
            var correctedNextRun = job.NextRunAt.Value;
            if (computedNext is not null && computedNext < correctedNextRun)
            {
                correctedNextRun = computedNext.Value;
                await _cronStore.SetNextRunAtAsync(job.Id, correctedNextRun, ct).ConfigureAwait(false);
            }

            // The effective wake is the LATER of the corrected cache and the job's own floor, so a
            // correction can pull the cache back without pulling the job's requested pacing back
            // with it. With no backoff - the overwhelmingly common case, and every row written
            // before #3350 - this is exactly the pre-existing computation.
            var effectiveNextRun = job.BackoffUntil is { } floor && floor > correctedNextRun
                ? floor
                : correctedNextRun;

            if (effectiveNextRun > now)
                continue;

            // The floor is consumed by the run it deferred: a spent backoff left in place would be
            // indistinguishable to a later reader from a live one.
            if (job.BackoffUntil is not null)
                await _cronStore.SetBackoffUntilAsync(job.Id, null, ct).ConfigureAwait(false);

            dueJobs.Add((job, expression));
        }

        if (dueJobs.Count == 0)
            return;

        // Phase 2 (bounded-concurrent): execute due jobs in parallel so a long-running agent prompt for
        // one job does not delay other due jobs or user-facing sessions -- but bounded by an aggregate
        // cap (#2670) so a synchronised tick cannot fan out an unbounded burst of billed model turns and
        // provider connections. The remainder queue and run as slots free; NOTHING is dropped.
        //
        // This bound is deliberately independent of the per-job _jobLocks semaphore, which answers a
        // different question (serialising repeat runs of ONE job).
        var maxConcurrency = _optionsMonitor.CurrentValue.MaxConcurrentJobs;
        if (maxConcurrency <= 0)
        {
            _logger.LogDebug(
                "Cron MaxConcurrentJobs was {Configured}; falling back to the default of {Default}.",
                maxConcurrency,
                CronOptions.DefaultMaxConcurrentJobs);
            maxConcurrency = CronOptions.DefaultMaxConcurrentJobs;
        }

        await Parallel.ForEachAsync(
            dueJobs,
            new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
            async (entry, _) =>
            {
                var (job, expression) = entry;
                var tz = CronTimeZoneResolver.Resolve(job.TimeZone, _logger, job.Id);
                await RunActionAsync(job, CronTriggerType.Scheduled, now, ct).ConfigureAwait(false);

                // #2133: reschedule via the narrow next_run_at write. RunActionAsync already
                // persisted the run's terminal LastRun* bookkeeping and any conversation pin
                // through their own narrow writes, so no whole-record round-trip is needed here.
                await _cronStore.SetNextRunAtAsync(job.Id, expression.NextRun(now, tz), ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    private async Task<CronRun> RunActionAsync(CronJob job, CronTriggerType triggerType, DateTimeOffset triggeredAt, CancellationToken ct)
    {
        // #2634 (fire time -- the AUTHORITATIVE expiry gate). Checked BEFORE the run row is stamped
        // so an expired job produces no run at all and, critically, never invokes its action.
        //
        // The schedule-time check in ProcessTickAsync alone is not sufficient: a manual RunNowAsync
        // never passes through the due scan, and a job that was already due can have its expiry
        // elapse between the scan and this call. Both would slip through. AC3 says the job "stops
        // executing after that instant", so the gate lives where execution actually begins.
        //
        // Suppression only. The stored job is not disabled, not deleted, and not rewritten -- #2634
        // explicitly rules out mutating an existing job implicitly.
        if (IsExpired(job))
        {
            _logger.LogInformation(
                "Cron job '{JobId}' ('{JobName}') is past its expiry ({ExpiresAt:o}); the fire was suppressed and the action was not invoked.",
                job.Id,
                job.Name,
                job.ExpiresAt);

            return new CronRun
            {
                Id = RunId.From(Guid.NewGuid().ToString("N")),
                JobId = job.Id,
                StartedAt = triggeredAt,
                CompletedAt = triggeredAt,
                Status = CronRunStatus.Skipped,
                Error = $"Job expired at {job.ExpiresAt:o}; execution suppressed."
            };
        }

        var run = await _cronStore.RecordRunStartAsync(job.Id, ct).ConfigureAwait(false);
        var action = ResolveAction(NormalizeActionType(job.ActionType));

        // #3160: from here on the run executes under its OWN linked token, not the caller's. That
        // token is published in _activeRuns so DeleteJobAsync / CancelActiveRunAsync have something
        // to signal - pre-#3160 the only cancellation source was a method local inside
        // ExecuteActionWithTimeoutAsync, unreachable from every mutation path. The link preserves
        // host cancellation exactly: a gateway shutdown still cancels `ct`, which still cancels
        // this. Registration happens immediately after the run row is stamped, so the window in
        // which a run exists but is uncancellable is a single store write rather than a whole turn.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var activeRun = new ActiveCronRun(job.Id, runCts);
        _activeRuns[run.Id.Value] = activeRun;
        var runCt = runCts.Token;

        // Serialize same-job runs in this process so the "create conversation -> CAS stamp"
        // window is single-threaded; concurrent triggers for OTHER jobs run unimpeded.
        var jobLock = _jobLocks.GetOrAdd(job.Id.Value, _ => new SemaphoreSlim(1, 1));
        try
        {
            // #3160: waiting on runCt, not ct, so a job deleted while this trigger is still QUEUED
            // behind a sibling run is released too. A queued run that only watched the host token
            // would sit there and then execute the action of a job that no longer exists.
            await jobLock.WaitAsync(runCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runCt.IsCancellationRequested)
        {
            if (activeRun.OperatorCancelled)
            {
                // Cancelled by an operator before it ever started: terminal, recorded as an
                // explicit abort, and NOT rethrown - nobody asked the caller to fail.
                await RecordAbortedRunAsync(run.Id, job, triggeredAt, CronRunStatus.Aborted, OperatorAbortReason)
                    .ConfigureAwait(false);
                await MaybeDeleteOneShotJobAsync(job).ConfigureAwait(false);
                ReleaseActiveRun(run.Id, activeRun);
                return run with { Status = CronRunStatus.Aborted, CompletedAt = _timeProvider.GetUtcNow(), Error = OperatorAbortReason };
            }

            // Aborted while waiting for the per-job lock (another same-job run held it during a
            // shutdown/cancel). The run was already stamped Running by RecordRunStartAsync above,
            // so record the abort here too - otherwise it stays stuck Running. CancellationToken.None
            // for the write since `ct` is cancelled.
            await RecordAbortedRunAsync(run.Id, job, triggeredAt).ConfigureAwait(false);
            // #2634 (AC2): a one-shot aborted before it even acquired the lock is still terminal --
            // it will never run again on its own -- so the job is removed here too. Leaving it out
            // would rebuild exactly the bug: an early-ending turn leaving the job scheduled forever.
            await MaybeDeleteOneShotJobAsync(job).ConfigureAwait(false);
            ReleaseActiveRun(run.Id, activeRun);
            throw;
        }

        // #2641 AC1: hoisted out of the try so the OUTER error catch can still read the cost the
        // action accumulated before it threw. Left null until assigned, so the catch distinguishes
        // "failed before a context existed" (nothing to record) from "failed after doing work"
        // (record it) - the difference between an honest NULL and a fabricated zero.
        CronExecutionContext? executionContext = null;
        try
        {
            // Re-read the job inside the lock — another in-process run may have already
            // pinned ConversationId between the time we entered RunActionAsync and now.
            var jobForRun = await _cronStore.GetAsync(job.Id, ct).ConfigureAwait(false) ?? job;

            using var scope = _scopeFactory.CreateScope();
            var context = new CronExecutionContext
            {
                Job = jobForRun,
                RunId = run.Id,
                TriggeredAt = triggeredAt,
                TriggerType = triggerType,
                Services = scope.ServiceProvider
            };
            executionContext = context;

            var timeoutSeconds = ResolveJobTimeout(jobForRun);

            // Opt-in ephemeral cleanup (#1561): once the action has run, delete the run's
            // cron-scoped session + transcript when the job requested it, exactly once, across
            // every terminal path (ok / timed_out / aborted / error). The finally fires before
            // the outer error catch, so the error path is covered too without a second cleanup.
            // Uses the per-run scope's ISessionStore (same seam as ReconcileCasLoserAsync).
            try
            {
                // Run the action under its timeout. The helper discriminates timeout-vs-host-cancel:
                // a host abort rethrows (handled by the catch below); a timeout returns its error
                // string; success returns null. This keeps the timeout/cancel discrimination out of
                // the body (no doubled try/try) so the terminal-status mapping is a flat decision.
                // #3160: the action runs under the run-scoped token so an operator delete/disable
                // can reach it. `runCt` is linked to `ct`, so host cancellation is unchanged.
                var timeoutError = await ExecuteActionWithTimeoutAsync(action, context, timeoutSeconds, runCt)
                    .ConfigureAwait(false);

                if (timeoutError is not null)
                {
                    // #2641 AC1: a timed-out run records the cost of the work it did before the
                    // timeout fired. This is the branch that matters most - a job that times out is
                    // by definition one that consumed its entire budget.
                    await _cronStore.RecordRunCompleteAsync(run.Id, CronRunStatus.TimedOut, timeoutError, cost: context.Cost, ct: ct)
                        .ConfigureAwait(false);
                    await FinalizeRunAsync(job.Id, jobForRun, triggeredAt, CronRunStatus.TimedOut, timeoutError, ct: ct)
                        .ConfigureAwait(false);
                    return run with { Status = CronRunStatus.TimedOut, CompletedAt = DateTimeOffset.UtcNow, Error = timeoutError };
                }

                _logger.LogInformation("Cron job executed: {JobName} ({JobId}) action={ActionType} trigger={TriggerType}",
                    jobForRun.Name, jobForRun.Id, jobForRun.ActionType, triggerType);

                // #2985 + #3161: the terminal outcome of a completed action is no longer
                // unconditionally Ok. Two independent conditions can demote it - an execution-class
                // job that made zero tool calls, and a run whose primary delivery failed - so the
                // decision lives in ONE resolver rather than being open-coded here, where a third
                // condition would inevitably be bolted on with a different precedence.
                var outcome = ResolveTerminalOutcome(jobForRun, context);

                if (outcome.Status is CronRunStatus.NoToolCalls)
                {
                    _logger.LogWarning(
                        "Cron job '{JobId}' ({JobName}) completed with zero tool invocations and is marked execution-class; recording status '{Status}'.",
                        jobForRun.Id, jobForRun.Name, CronRunStatus.NoToolCalls);
                }
                else if (outcome.Status is CronRunStatus.DeliveryFailed)
                {
                    // #3161: the loudest case in the whole method. The turn worked; the output went
                    // nowhere. Pre-#3161 this logged nothing at all and recorded 'ok'.
                    _logger.LogError(
                        "Cron job '{JobId}' ({JobName}) completed but its primary delivery failed; recording status '{Status}'. Delivery error: {DeliveryError}",
                        jobForRun.Id, jobForRun.Name, CronRunStatus.DeliveryFailed, context.DeliveryError);
                }

                var terminalStatus = outcome.Status;
                var terminalError = outcome.Error;

                await _cronStore.RecordRunCompleteAsync(run.Id, terminalStatus, terminalError, sessionId: context.SessionId, cost: context.Cost, ct: ct).ConfigureAwait(false);

                // Pinback via CAS: if the trigger created a new conversation for this run and the job
                // has no pinned ConversationId yet, atomically stamp ours onto the job. If another
                // run won the race (multi-process), archive ours and rebind our session to the winner.
                var winningConversationId = await TryPinConversationAsync(job.Id, jobForRun, context, scope.ServiceProvider, ct)
                    .ConfigureAwait(false);

                await FinalizeRunAsync(job.Id, jobForRun, triggeredAt, terminalStatus, error: terminalError,
                    conversationId: winningConversationId, ct: ct).ConfigureAwait(false);

                if (terminalError is not null)
                {
                    // Reuse the EXISTING failure-alert path (#2557) rather than adding a parallel
                    // notification channel: the whole point of #2985 is that run status is the
                    // input to alerting, so making the outcome non-success is what makes alerting
                    // possible at all. #3161 extends the same reasoning to delivery failure.
                    var alertFailure = await MaybeSendFailureAlertAsync(jobForRun, triggeredAt, terminalError, ct).ConfigureAwait(false);

                    // #3161 AC3: fail CLOSED. An alert that could not be delivered used to leave a
                    // single Error log line and nothing else - no row, no query, nothing an operator
                    // would ever see. Fold it into the run's recorded error so the double failure is
                    // discoverable from run history. The run's STATUS is deliberately left alone,
                    // preserving the #2557 AC7 containment that alert delivery never alters the run's
                    // own outcome.
                    terminalError = await RecordAlertDeliveryFailureAsync(
                        run.Id, job.Id, triggeredAt, terminalStatus, terminalError, terminalError, alertFailure, ct).ConfigureAwait(false);
                }

                return run with { Status = terminalStatus, CompletedAt = DateTimeOffset.UtcNow, Error = terminalError, SessionId = context.SessionId };
            }
            catch (OperationCanceledException) when (runCt.IsCancellationRequested)
            {
                // #3160: an operator deleted or disabled the job while this run was executing. That
                // is a DELIBERATE terminal outcome, not a failure: it is recorded as `aborted` (a
                // status distinct from both `error` and `timed_out`), it emits no failure alert, and
                // it is NOT rethrown - the caller asked for the run to stop, so surfacing an
                // OperationCanceledException to them would report their own successful request as a
                // fault. Host cancellation keeps its pre-#3160 behaviour verbatim in the branch below.
                if (activeRun.OperatorCancelled)
                {
                    _logger.LogInformation(
                        "Cron run aborted by operator (job deleted or disabled while running). JobId: {JobId}, RunId: {RunId}",
                        job.Id, run.Id);
                    await RecordAbortedRunAsync(run.Id, job, triggeredAt, CronRunStatus.Aborted, OperatorAbortReason, context.Cost)
                        .ConfigureAwait(false);
                    return run with
                    {
                        Status = CronRunStatus.Aborted,
                        CompletedAt = _timeProvider.GetUtcNow(),
                        Error = OperatorAbortReason,
                        SessionId = context.SessionId
                    };
                }

                // The run was aborted via the host token (gateway shutdown, scheduler stop, or an
                // explicit cancel of a manual run) rather than the per-job timeout. Without this
                // branch the cancellation would leave the run permanently in the Running state it
                // was stamped with at RecordRunStartAsync - a silent non-success that masquerades as
                // never having finished. Record it as a failed run so the abort is visible, then
                // rethrow to preserve cancellation semantics for the caller/host shutdown.
                _logger.LogWarning(
                    "Cron job aborted (cancellation requested). JobId: {JobId}, ActionType: {ActionType}",
                    job.Id, job.ActionType);
                await RecordAbortedRunAsync(run.Id, job, triggeredAt, cost: context.Cost).ConfigureAwait(false);
                throw;
            }
            finally
            {
                await MaybeDeleteEphemeralRunSessionAsync(jobForRun, context, scope.ServiceProvider).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!runCt.IsCancellationRequested)
        {
            // #3209: the FULL exception - type name, stack trace, build-machine source paths -
            // stays here, in the structured log, which is the right home for it: a developer with
            // log access loses nothing. Everything below records the sanitized PROJECTION instead,
            // because run history is durable in cron.sqlite and the alert path re-exports it into a
            // conversation an agent can read.
            _logger.LogError(ex, "Cron job execution failed. JobId: {JobId}, ActionType: {ActionType}", job.Id, job.ActionType);
            var projectedError = CronErrorProjection.Project(ex);
            // #2641 AC1: a run that threw still cost what it cost. Passing the accumulated context
            // cost here is what makes the acceptance criterion's "a failed run still records the
            // cost of the work it did before failing" true on the error path, not just the timeout
            // one.
            await _cronStore.RecordRunCompleteAsync(run.Id, CronRunStatus.Error, ex.Message, cost: executionContext?.Cost, ct: ct).ConfigureAwait(false);
            await FinalizeRunAsync(job.Id, job, triggeredAt, CronRunStatus.Error, projectedError, ct: ct).ConfigureAwait(false);
            var errorPathAlertFailure = await MaybeSendFailureAlertAsync(job, triggeredAt, projectedError, ct).ConfigureAwait(false);
            // #3161 AC3: same fail-closed recording on the error path. The containment seam is
            // shared, so recording it for one terminal outcome only would leave the commonest
            // failure shape - a broken job whose alert channel is ALSO broken - still invisible.
            var recordedError = await RecordAlertDeliveryFailureAsync(
                run.Id, job.Id, triggeredAt, CronRunStatus.Error, ex.Message, projectedError, errorPathAlertFailure, ct).ConfigureAwait(false);
            return run with { Status = CronRunStatus.Error, CompletedAt = DateTimeOffset.UtcNow, Error = recordedError };
        }
        catch (Exception ex) when (activeRun.OperatorCancelled)
        {
            // #3160: an operator cancel that surfaced as something other than an OperationCanceledException
            // (an action that wraps or translates its cancellation). The operator's intent is what
            // decides the outcome, not the exception type the action happened to choose, so this is
            // still an abort rather than an error - and still no alert.
            _logger.LogInformation(
                ex,
                "Cron run aborted by operator; the action surfaced {ExceptionType} rather than a cancellation. JobId: {JobId}, RunId: {RunId}",
                ex.GetType().Name, job.Id, run.Id);
            await RecordAbortedRunAsync(run.Id, job, triggeredAt, CronRunStatus.Aborted, OperatorAbortReason)
                .ConfigureAwait(false);
            return run with { Status = CronRunStatus.Aborted, CompletedAt = _timeProvider.GetUtcNow(), Error = OperatorAbortReason };
        }
        finally
        {
            // #2634 (AC1/AC2): scheduler-driven one-shot removal. This sits in the SAME outermost
            // finally that releases the per-job lock -- one level up from the finally that hosts
            // MaybeDeleteEphemeralRunSessionAsync -- so it runs on EVERY terminal path: success,
            // timeout, an action that threw, and a host cancellation that rethrows past the outer
            // catch. Removal driven from the success path only would reproduce the original defect,
            // where a job whose agent turn ended early was never cleaned up.
            //
            // Ordering: the lock is released first so the delete cannot deadlock against a
            // same-job waiter, and the delete is best-effort (never throws out of the finally).
            jobLock.Release();

            // #3160: deregister BEFORE MaybeDeleteOneShotJobAsync. That call routes through
            // DeleteJobAsync, which now waits for this job's active runs to observe cancellation -
            // and this run IS one of them. Releasing afterwards would have the run wait on itself
            // for the full grace period on every one-shot job. Ordering here is load-bearing, not
            // cosmetic.
            ReleaseActiveRun(run.Id, activeRun);

            await MaybeDeleteOneShotJobAsync(job).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reason recorded on a run terminated because an operator deleted or disabled its job (#3160).
    /// Held as a constant so the producer and the tests that pin the clause cannot drift apart.
    /// </summary>
    internal const string OperatorAbortReason = "Cron run aborted: the job was deleted or disabled by an operator while the run was executing.";

    /// <summary>
    /// Removes an in-flight run from the registry, disposes its cancellation source, and signals
    /// waiters that the run has observed its cancellation and left the action body (#3160 AC5/AC6).
    /// </summary>
    /// <remarks>
    /// Idempotent and non-throwing: it is reached from several terminal paths and from a
    /// <c>finally</c>, so it must never be the thing that fails a run. The <b>signal comes before
    /// the dispose</b> so a waiter released by <see cref="Completed"/> can never observe a
    /// half-disposed entry.
    /// </remarks>
    private void ReleaseActiveRun(RunId runId, ActiveCronRun activeRun)
    {
        _activeRuns.TryRemove(new KeyValuePair<string, ActiveCronRun>(runId.Value, activeRun));
        activeRun.Completed.TrySetResult();
        try
        {
            activeRun.Cts.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing the cancellation source for cron run '{RunId}' failed; the entry was still deregistered.", runId);
        }
    }

    /// <summary>
    /// Cancels every run of <paramref name="jobId"/> currently in flight in this process and waits
    /// (bounded) for each to observe that cancellation (#3160). Returns how many runs were signalled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the shared seam behind both operator paths - <see cref="DeleteJobAsync"/> and the
    /// disable half of a definition update. Matching is an EXACT ordinal job-id comparison: a loose
    /// or prefix match here is how removing <c>job-1</c> would also kill <c>job-10</c>'s run.
    /// </para>
    /// <para>
    /// The wait is a grace period, never a guarantee. An action that swallows its cancellation token
    /// must not be able to make its job permanently undeletable, so when
    /// <see cref="CronOptions.ActiveRunCancellationGraceSeconds"/> elapses the method logs and
    /// returns - the operator's removal always wins.
    /// </para>
    /// </remarks>
    public async Task<int> CancelActiveRunAsync(JobId jobId, CancellationToken cancellationToken = default)
        => (await CancelActiveRunsAsync(jobId, cancellationToken).ConfigureAwait(false)).Signalled;

    /// <summary>
    /// Outcome of a cancellation sweep: how many runs were signalled, and whether every one of them
    /// was actually seen to leave its action body before the grace period elapsed (#3517).
    /// </summary>
    /// <param name="Signalled">How many in-flight runs were signalled. Zero means the job was idle.</param>
    /// <param name="Observed">
    /// <c>true</c> when every signalled run observed its cancellation (trivially true when none were
    /// signalled). <c>false</c> means at least one run is STILL EXECUTING and still holds whatever
    /// the run holds - which is precisely when a follow-on teardown step must not be attempted.
    /// </param>
    internal readonly record struct CancellationSweep(int Signalled, bool Observed);

    /// <summary>
    /// The <see cref="CancelActiveRunAsync"/> body, reporting whether cancellation was OBSERVED as
    /// well as how many runs were signalled (#3517).
    /// </summary>
    /// <remarks>
    /// The distinction is the whole of #3517. <see cref="CancelActiveRunAsync"/> returns a count,
    /// which tells a caller nothing about whether the runs are gone - so <see cref="DeleteJobAsync"/>
    /// proceeded into an archive that a still-live run made impossible, failed, and retried forever.
    /// </remarks>
    internal async Task<CancellationSweep> CancelActiveRunsAsync(JobId jobId, CancellationToken cancellationToken = default)
    {
        var matches = _activeRuns
            .Where(entry => entry.Value.Job == jobId)
            .Select(entry => entry.Value)
            .ToList();

        if (matches.Count == 0)
            return new CancellationSweep(0, Observed: true);

        foreach (var active in matches)
            active.RequestOperatorCancel();

        _logger.LogInformation(
            "Cancelled {Count} in-flight cron run(s) for job '{JobId}' after an operator delete/disable.",
            matches.Count,
            jobId);

        var graceSeconds = _optionsMonitor.CurrentValue?.ActiveRunCancellationGraceSeconds ?? 30;
        if (graceSeconds <= 0)
        {
            // No grace configured means no opportunity to observe. Report that honestly rather than
            // claiming an observation that was never waited for.
            return new CancellationSweep(matches.Count, Observed: false);
        }

        // Linked source so the grace timer is torn down the instant the runs are observed, rather
        // than being left pending on the TimeProvider for the whole grace period on every delete.
        using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var grace = Task.Delay(TimeSpan.FromSeconds(graceSeconds), _timeProvider, graceCts.Token);
        var observed = Task.WhenAll(matches.Select(active => active.Completed.Task));

        var winner = await Task.WhenAny(observed, grace).ConfigureAwait(false);
        await graceCts.CancelAsync().ConfigureAwait(false);

        if (winner != observed)
        {
            // Fail OPEN. Blocking the delete forever on an uncooperative action would convert a
            // runaway job into an unremovable one - strictly worse than the race this wait avoids.
            _logger.LogWarning(
                "Cron job '{JobId}' had {Count} in-flight run(s) that did not observe cancellation within {Grace}s; "
                + "proceeding with the delete/disable anyway.",
                jobId,
                matches.Count,
                graceSeconds);
        }

        return new CancellationSweep(matches.Count, Observed: winner == observed);
    }

    /// <summary>
    /// Whether <paramref name="job"/> is past its <see cref="CronJob.ExpiresAt"/> instant (#2634).
    /// </summary>
    /// <remarks>
    /// A <c>null</c> expiry is <b>never</b> expired: NULL means "no expiry", so a job that does not
    /// carry the field behaves exactly as it does today (AC4). The comparison is inclusive
    /// (<c>&gt;=</c>) so the expiry instant itself is already past -- "stops executing after that
    /// instant" must not leave a one-tick window where a fire still lands.
    /// </remarks>
    private bool IsExpired(CronJob job)
        => job.ExpiresAt is { } expiresAt && _timeProvider.GetUtcNow() >= expiresAt;

    /// <summary>
    /// Opt-in scheduler-driven one-shot removal (#2634): deletes the <b>job</b> after its first
    /// terminal run when <see cref="CronJob.DeleteJobAfterRun"/> is set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the job-scoped analogue of <see cref="MaybeDeleteEphemeralRunSessionAsync"/>
    /// (#1561), which deletes the run's <i>session</i>. The two are independent and compose; neither
    /// changes the other's semantics.
    /// </para>
    /// <para>
    /// The job is re-read first, so an opt-in toggled off between trigger and completion is honoured
    /// and a job already deleted mid-run is a no-op. Deletion goes through
    /// <see cref="DeleteJobAsync"/> so the job's pinned conversation is archived exactly as it is for
    /// a manual delete.
    /// </para>
    /// <para>
    /// Best-effort and <see cref="CancellationToken.None"/>: this runs from a <c>finally</c> that is
    /// frequently reached during host shutdown (where the caller's token is already cancelled) and on
    /// the rethrow path of a cancelled run. A failure here is logged and swallowed so it can never
    /// mask the run's real outcome or escape the finally.
    /// </para>
    /// </remarks>
    private async Task MaybeDeleteOneShotJobAsync(CronJob job)
    {
        if (!job.DeleteJobAfterRun)
            return;

        // #3517: already terminal. Once the bound is reached the job has been disabled and no
        // further deletion is attempted for the life of this process - re-entering here would let
        // the attempt count (and the error) keep growing, which is the defect.
        if (_oneShotDeleteFailures.TryGetValue(job.Id.Value, out var priorFailures) && priorFailures >= MaxOneShotDeleteAttempts)
        {
            _logger.LogDebug(
                "One-shot removal not re-attempted for job '{JobId}': it already failed {Attempts} times and was disabled.",
                job.Id,
                priorFailures);
            return;
        }

        try
        {
            var latest = await _cronStore.GetAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
            if (latest is null)
            {
                _oneShotDeleteFailures.TryRemove(job.Id.Value, out _);
                return;
            }

            if (!latest.DeleteJobAfterRun)
            {
                _oneShotDeleteFailures.TryRemove(job.Id.Value, out _);
                _logger.LogDebug(
                    "One-shot removal skipped for job '{JobId}': deleteJobAfterRun was cleared while the run was in flight.",
                    job.Id);
                return;
            }

            await DeleteJobAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
            _oneShotDeleteFailures.TryRemove(job.Id.Value, out _);
            _logger.LogInformation(
                "Deleted one-shot cron job '{JobId}' ('{JobName}') after its terminal run (deleteJobAfterRun).",
                job.Id,
                job.Name);
        }
        catch (Exception ex)
        {
            var failures = _oneShotDeleteFailures.AddOrUpdate(job.Id.Value, 1, static (_, current) => current + 1);

            if (failures < MaxOneShotDeleteAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to delete one-shot cron job '{JobId}' after its run (attempt {Attempt} of {Max}). The run outcome is "
                    + "unaffected; removal will be retried after the next run.",
                    job.Id,
                    failures,
                    MaxOneShotDeleteAttempts);
                return;
            }

            // #3517: the bound. Past this point retrying is not a policy, it is a stuck job
            // re-emitting an identical error every schedule interval forever - 81% of the platform's
            // whole error budget in the reported window. Drive the job to a TERMINAL state instead
            // and say so exactly once.
            await DisableUndeletableOneShotJobAsync(job, failures, ex).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Terminal state for a one-shot job whose deletion has failed <see cref="MaxOneShotDeleteAttempts"/>
    /// times: disabled, so it stops firing, with a single actionable error (#3517).
    /// </summary>
    /// <remarks>
    /// Disabling rather than force-deleting is deliberate. The delete failed for a reason nobody has
    /// diagnosed yet, and silently dropping the row would destroy the only evidence an operator has.
    /// A disabled job stops consuming a provider round-trip every interval, stops re-emitting the
    /// error, and stays visible for a human to inspect - which is what "terminal" has to mean here.
    /// </remarks>
    private async Task DisableUndeletableOneShotJobAsync(CronJob job, int failures, Exception cause)
    {
        try
        {
            var latest = await _cronStore.GetAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
            if (latest is null)
            {
                _oneShotDeleteFailures.TryRemove(job.Id.Value, out _);
                return;
            }

            await _cronStore.UpdateDefinitionAsync(latest with { Enabled = false }, CancellationToken.None).ConfigureAwait(false);
            await _cronStore.SetNextRunAtAsync(job.Id, null, CancellationToken.None).ConfigureAwait(false);

            _logger.LogError(
                cause,
                "One-shot cron job '{JobId}' ('{JobName}') could not be deleted after {Attempts} attempts and has been "
                + "DISABLED so it stops firing. This is the terminal outcome: no further deletion attempts will be made. "
                + "Investigate the cause below and remove the job manually.",
                job.Id,
                job.Name,
                failures);
        }
        catch (Exception ex)
        {
            // Best-effort to the last: this runs from a finally and must never escape it.
            _logger.LogError(
                ex,
                "One-shot cron job '{JobId}' could not be deleted after {Attempts} attempts, and disabling it also failed. "
                + "The job requires manual removal.",
                job.Id,
                failures);
        }
    }

    /// <summary>
    /// Opt-in per-job failure alerting (#2557). Called on the run's transition to
    /// <see cref="CronRunStatus.Error"/>, after the terminal run row has been written so the
    /// consecutive-error streak can be derived from the existing run history rather than from a
    /// second counter column that could drift from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Delivery is best-effort and <b>never</b> fails the cron run: the run's own terminal state
    /// has already been persisted by the time this runs, and every exception out of the sink is
    /// caught and logged (AC7). A broken alert channel must not convert a job failure into a
    /// second, different failure.
    /// </para>
    /// <para>
    /// Error text crosses an external-delivery boundary, so it is routed through
    /// <see cref="CronExternalDeliveryRedactor.RedactSummary"/> - the redaction seam that already
    /// existed for exactly this purpose.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <c>null</c> when the alert was delivered, skipped, or not applicable; otherwise the
    /// alert-delivery failure text, so the caller can fail closed and record it (#3161 AC3). This
    /// method still never throws.
    /// </returns>
    private async Task<string?> MaybeSendFailureAlertAsync(
        CronJob job,
        DateTimeOffset scheduledRunTime,
        string? error,
        CancellationToken ct)
    {
        try
        {
            // Re-read so an alert opt-in toggled between trigger and failure is honoured, and so
            // a job deleted mid-run does not alert at all.
            var latest = await _cronStore.GetAsync(job.Id, ct).ConfigureAwait(false);
            if (latest is null || !latest.FailureAlertsEnabled)
                return null;

            if (latest.FailureAlertConversationId is not { } conversationId)
            {
                _logger.LogWarning(
                    "Cron failure alert skipped: job '{JobId}' has alerts enabled but no FailureAlertConversationId configured.",
                    job.Id);
                return null;
            }

            var consecutiveErrors = await CountConsecutiveErrorsAsync(job.Id, ct).ConfigureAwait(false);
            if (!ShouldAlertForStreakPosition(consecutiveErrors))
                return null;

            using var scope = _scopeFactory.CreateScope();
            var sink = scope.ServiceProvider.GetService<ICronFailureAlertSink>();
            if (sink is null)
            {
                _logger.LogWarning(
                    "Cron failure alert skipped: no ICronFailureAlertSink is registered (job '{JobId}').",
                    job.Id);
                return null;
            }

            var redactor = scope.ServiceProvider.GetService<ISecretRedactor>();
            var redactedError = redactor is null
                ? null
                : CronExternalDeliveryRedactor.RedactSummary(redactor, error);

            var alert = new CronFailureAlert(
                JobId: latest.Id,
                JobName: latest.Name,
                ScheduledRunTime: scheduledRunTime,
                AttemptedAt: DateTimeOffset.UtcNow,
                ConsecutiveErrorCount: consecutiveErrors,
                Error: redactedError);

            await sink.SendAsync(conversationId, alert, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Cron failure alert delivered for job '{JobId}' (consecutiveErrors={ConsecutiveErrors}).",
                latest.Id, consecutiveErrors);
            return null;
        }
        catch (Exception alertEx)
        {
            // AC7: never let alert delivery fail the run. The run is already finalized above.
            _logger.LogError(
                alertEx,
                "Cron failure alert delivery failed for job '{JobId}'. The cron run itself is unaffected.",
                job.Id);
            // #3161 AC3: the containment is preserved exactly - nothing is rethrown - but the
            // failure is no longer *discarded*. It is returned so the caller can fail CLOSED and
            // record it against the run, because a single Error log line is not an observable
            // outcome: nothing queries it and no operator reads it.
            return alertEx.Message;
        }
    }

    /// <summary>
    /// #3161 AC3: folds an alert-delivery failure into the run's recorded error so the double
    /// failure (the run failed AND nobody could be told) is discoverable from run history rather
    /// than existing only as a log line. Returns the error text now recorded on the run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The run's <b>status</b> is deliberately left untouched and is re-written verbatim. That is
    /// the #2557 AC7 containment this issue's AC5 requires preserving: a broken alert channel must
    /// never convert one failure into a second, different one. Only the error text grows.
    /// </para>
    /// <para>
    /// This helper is itself fully contained. If the store write fails we are already deep in a
    /// double-failure path; throwing here would turn an observability improvement into a new way
    /// for a cron run to blow up.
    /// </para>
    /// </remarks>
    /// <param name="runId">The run whose history row is amended.</param>
    /// <param name="jobId">The job whose <c>LastRunError</c> is amended.</param>
    /// <param name="triggeredAt">The run's trigger instant, re-stamped unchanged.</param>
    /// <param name="terminalStatus">The already-decided terminal status, re-written unchanged.</param>
    /// <param name="runError">Error text already recorded on the run-history row.</param>
    /// <param name="finalizationError">Error text already recorded as the job's <c>LastRunError</c>.</param>
    /// <param name="alertFailure">The alert-delivery failure, or <c>null</c> when delivery was fine.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The (possibly amended) error text now recorded on the run.</returns>
    private async Task<string?> RecordAlertDeliveryFailureAsync(
        RunId runId,
        JobId jobId,
        DateTimeOffset triggeredAt,
        string terminalStatus,
        string? runError,
        string? finalizationError,
        string? alertFailure,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(alertFailure))
            return runError;

        var suffix = $"{AlertDeliveryFailurePrefix}{alertFailure}";
        var amendedRunError = Append(runError, suffix);
        var amendedFinalizationError = Append(finalizationError, suffix);

        try
        {
            await _cronStore.RecordRunCompleteAsync(runId, terminalStatus, amendedRunError, ct: ct).ConfigureAwait(false);
            await _cronStore.RecordRunFinalizationAsync(jobId, triggeredAt, terminalStatus, amendedFinalizationError, ct).ConfigureAwait(false);
        }
        catch (Exception recordEx)
        {
            _logger.LogError(
                recordEx,
                "Failed to record the cron alert-delivery failure against run '{RunId}' of job '{JobId}'.",
                runId, jobId);
        }

        return amendedRunError;

        static string Append(string? existing, string addition)
            => string.IsNullOrWhiteSpace(existing) ? addition : $"{existing} {addition}";
    }

    /// <summary>
    /// Marker prefixed to the recorded error when a failure alert could not be delivered (#3161
    /// AC3). Held as a constant so the fail-closed test and the producer cannot drift apart, and so
    /// an operator scanning run history has one exact string to search for.
    /// </summary>
    internal const string AlertDeliveryFailurePrefix = "Failure alert could not be delivered: ";

    /// <summary>
    /// Length of the current unbroken error streak, derived from the newest run-history rows.
    /// Returns at least 1 when called immediately after an error row was written.
    /// </summary>
    private async Task<int> CountConsecutiveErrorsAsync(JobId jobId, CancellationToken ct)
    {
        var history = await _cronStore.GetRunHistoryAsync(jobId, FailureAlertHistoryWindow, ct).ConfigureAwait(false);
        var streak = 0;
        foreach (var entry in history)
        {
            // History is newest-first; a non-error terminal outcome ends the streak. Rows still
            // stamped Running are in-flight concurrent runs and are skipped rather than treated
            // as a break, so a parallel run cannot silently reset the backoff.
            if (string.Equals(entry.Status, CronRunStatus.Running, StringComparison.OrdinalIgnoreCase))
                continue;
            // #2985: a no_tool_calls run is a non-success outcome and belongs to the same streak
            // as an error. Counting only Error here would restart the backoff at 1 on every
            // zero-tool run, so a job stuck in the do-nothing state would alert on EVERY run -
            // reproducing the noise the #2557 backoff exists to prevent.
            if (!IsAlertableFailureStatus(entry.Status))
                break;
            streak++;
        }

        return streak == 0 ? 1 : streak;
    }

    /// <summary>
    /// Terminal statuses that count toward the failure-alert streak (#2557 + #2985): the action
    /// threw, or an execution-class run completed having done nothing. Both are non-success and
    /// both are things an operator wants alerted on with the same backoff.
    /// </summary>
    private static bool IsAlertableFailureStatus(string status)
        => string.Equals(status, CronRunStatus.Error, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, CronRunStatus.NoToolCalls, StringComparison.OrdinalIgnoreCase)
            // #3161: a delivery failure is a non-success outcome of exactly the same standing. Left
            // out, the streak would restart at 1 on every undelivered run, so the power-of-two
            // backoff would alert on EVERY run of a job whose destination is permanently gone -
            // rebuilding the noise #2557's backoff exists to prevent.
            || string.Equals(status, CronRunStatus.DeliveryFailed, StringComparison.OrdinalIgnoreCase);

    // #3160 (AC4): `aborted` is deliberately ABSENT from the set above. An operator who deleted or
    // disabled a job does not want to be alarmed about the run they themselves killed, and counting
    // their own action toward the error streak would additionally distort the backoff position of
    // the next GENUINE failure. Note that CountConsecutiveErrorsAsync treats any non-alertable
    // terminal status as a streak BREAK, which is the correct reading here: a deliberate stop says
    // nothing about the job's health either way.

    /// <summary>
    /// Backoff schedule (AC5): alert on the FIRST failure of a streak, then on positions that are
    /// exact powers of two (1, 2, 4, 8, 16, ...). Without this a job failing every minute would
    /// deliver an alert every minute - becoming the noise the alert was meant to detect.
    /// </summary>
    private static bool ShouldAlertForStreakPosition(int consecutiveErrors)
    {
        if (consecutiveErrors <= 1)
            return true;

        // Power-of-two test: exactly one bit set.
        return (consecutiveErrors & (consecutiveErrors - 1)) == 0;
    }

    /// <summary>
    /// How far back the streak scan reads. Bounded so a job failing for months does not pull an
    /// unbounded history; beyond this the streak simply saturates, which only makes the backoff
    /// more conservative.
    /// </summary>
    private const int FailureAlertHistoryWindow = 64;

    /// <summary>
    /// THE single decision point for a completed action's terminal outcome (#2985 + #3161).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two independent conditions can demote a completed run, and their <b>precedence is
    /// deliberate</b>: a delivery failure outranks the zero-tool-call outcome. If the output never
    /// reached its destination, that is the operator's actionable problem and the thing they must
    /// fix; "and it also made no tool calls" is a downstream detail. Recording the zero-tool status
    /// instead would point the operator at the agent when the conversation is what is broken.
    /// </para>
    /// <para>
    /// Kept as one resolver rather than two open-coded branches at the call site so a future third
    /// condition has an obvious home and cannot be bolted on with an accidentally different
    /// precedence.
    /// </para>
    /// </remarks>
    /// <param name="job">The job as re-read inside the per-job lock.</param>
    /// <param name="context">The completed execution context carrying the action's reports.</param>
    /// <returns>The terminal status and its recorded reason (<c>null</c> reason for a clean success).</returns>
    private static (string Status, string? Error) ResolveTerminalOutcome(CronJob job, CronExecutionContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.DeliveryError))
            return (CronRunStatus.DeliveryFailed, $"{DeliveryFailureReasonPrefix}{context.DeliveryError}");

        var zeroToolCallOutcome = DetectZeroToolCallOutcome(job, context);
        return zeroToolCallOutcome is null
            ? (CronRunStatus.Ok, null)
            : (CronRunStatus.NoToolCalls, zeroToolCallOutcome);
    }

    /// <summary>
    /// Prefix on the reason recorded for a <see cref="CronRunStatus.DeliveryFailed"/> run (#3161
    /// AC1: the recorded reason must name the condition). A constant so the test that pins the
    /// clause and the producer cannot drift apart.
    /// </summary>
    internal const string DeliveryFailureReasonPrefix =
        "Cron run completed but its primary delivery failed - the output reached nobody: ";

    /// <summary>
    /// #2985 clause 1: decides whether a completed action must terminate as
    /// <see cref="CronRunStatus.NoToolCalls"/> instead of <see cref="CronRunStatus.Ok"/>, and
    /// returns the human-readable reason recorded on the run (naming the zero-tool-call condition)
    /// or <c>null</c> when the run is a normal success.
    /// </summary>
    /// <remarks>
    /// <para>
    /// BOTH conditions are required, and each guards a different way of getting this wrong:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>ExecutionClass</c> (clause 4) - an unmarked job is untouched. A reporting or
    /// classification job may legitimately answer from context with no tool call, and demoting
    /// those would make the signal worthless within a day.
    /// </description></item>
    /// <item><description>
    /// A <b>non-null</b> count - <c>null</c> means the action reported nothing (command, webhook,
    /// or an interrupted turn that already has its own terminal outcome). Reading that silence as
    /// zero would classify every shell job on the platform as a do-nothing run. Null is not zero.
    /// </description></item>
    /// </list>
    /// </remarks>
    private static string? DetectZeroToolCallOutcome(CronJob job, CronExecutionContext context)
    {
        if (!job.ExecutionClass)
            return null;

        if (context.ToolInvocationCount is not { } toolInvocationCount)
            return null;

        if (toolInvocationCount > 0)
            return null;

        return ZeroToolCallReason;
    }

    /// <summary>
    /// Reason text recorded on a <see cref="CronRunStatus.NoToolCalls"/> run (#2985 clause 1: the
    /// recorded reason must name the zero-tool-call condition). Held as a constant so the test
    /// that pins the clause and the producer cannot drift apart.
    /// </summary>
    internal const string ZeroToolCallReason =
        "Execution-class cron run completed with zero tool calls - the run performed no work.";

    /// <summary>
    /// Executes the action under its per-job timeout, discriminating a <i>timeout</i> from a
    /// <i>host cancellation</i>. Returns <c>null</c> on success, the timeout error message when the
    /// action exceeded <paramref name="timeoutSeconds"/>, and rethrows <see cref="OperationCanceledException"/>
    /// when the host token (<paramref name="ct"/>) was cancelled (gateway shutdown / scheduler stop /
    /// explicit cancel) so the caller can record the abort and propagate cancellation.
    /// </summary>
    private async Task<string?> ExecuteActionWithTimeoutAsync(
        ICronAction action,
        CronExecutionContext context,
        int? timeoutSeconds,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // #2904: a null timeout is the explicit "unlimited" sentinel. Arming no CancelAfter is the
        // whole point - the linked source still exists so the ambient token (gateway shutdown /
        // explicit cancel) cancels the run promptly, but nothing else can end it (AC2).
        if (timeoutSeconds is int armedTimeout)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(armedTimeout));

        // #2641 AC2: the scheduler owns the run clock. Stamped in a finally so it is recorded on
        // EVERY exit - success, timeout, and host/operator cancellation alike - because a run that
        // failed after 40 minutes is exactly the run an operator most needs the duration of.
        var startedAt = _timeProvider.GetUtcNow();
        try
        {
            await action.ExecuteAsync(context, timeoutCts.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Cron job timed out after {TimeoutSeconds}s. JobId: {JobId}, ActionType: {ActionType}",
                timeoutSeconds, context.Job.Id, context.Job.ActionType);
            return $"Job exceeded {timeoutSeconds}s timeout";
        }
        finally
        {
            context.RecordDuration((long)(_timeProvider.GetUtcNow() - startedAt).TotalMilliseconds);
        }
    }

    /// <summary>
    /// CAS pinback for a run that created a new conversation: when the job has no pinned
    /// <c>ConversationId</c> yet, atomically stamp this run's conversation onto the job. If another
    /// run (in another process) won the race, archive ours and rebind our session to the winner.
    /// Returns the conversation id that should be persisted onto the job (the winner, our own, or
    /// the existing value when nothing was created).
    /// </summary>
    private async Task<ConversationId?> TryPinConversationAsync(
        JobId jobId,
        CronJob jobForRun,
        CronExecutionContext context,
        IServiceProvider services,
        CancellationToken ct)
    {
        if (!context.ConversationId.HasValue)
        {
            return null;
        }

        // Re-read after action so we don't observe a stale pin (concurrent same-job runs / edits).
        var latest = await _cronStore.GetAsync(jobId, ct).ConfigureAwait(false) ?? jobForRun;
        if (latest.ConversationId.HasValue)
        {
            return context.ConversationId;
        }

        var winner = await _cronStore.TrySetConversationIdAsync(jobId, context.ConversationId.Value, ct)
            .ConfigureAwait(false);

        if (winner.HasValue && winner.Value == context.ConversationId.Value)
        {
            _logger.LogInformation(
                "Cron job pinned conversation. JobName: {JobName}, JobId: {JobId}, ConversationId: {ConversationId}",
                jobForRun.Name, jobForRun.Id, context.ConversationId.Value);
            return context.ConversationId;
        }

        if (winner.HasValue)
        {
            await ReconcileCasLoserAsync(
                services,
                loserConversationId: context.ConversationId.Value,
                winnerConversationId: winner.Value,
                sessionId: context.SessionId,
                ct: ct).ConfigureAwait(false);
            return winner;
        }

        // winner.HasValue == false means the job was deleted while we ran — leave the conversation
        // orphaned; the operator deleted the job so they no longer want it.
        return context.ConversationId;
    }

    /// <summary>
    /// The single "re-read latest job → narrow <see cref="ICronStore.RecordRunFinalizationAsync"/> write of the terminal
    /// <c>LastRun*</c> fields" write-back shared by every terminal path (ok / timed_out / error /
    /// aborted). Re-reading inside the write avoids clobbering concurrent edits (schedule updates).
    /// Optionally carries a resolved <paramref name="conversationId"/> for the success path's CAS
    /// pinback; all other paths leave the existing conversation untouched.
    /// </summary>
    private async Task FinalizeRunAsync(
        JobId jobId,
        CronJob fallback,
        DateTimeOffset triggeredAt,
        string status,
        string? error,
        ConversationId? conversationId = null,
        CancellationToken ct = default)
    {
        _ = fallback;
        // #2133: terminal bookkeeping is a narrow last_run_* write that leaves definition
        // columns, next_run_at, and the conversation pin untouched. This is what makes run
        // finalization racing a concurrent definition edit safe - it can no longer overwrite
        // the edit. The conversation pin (conversationId) was already persisted atomically by
        // TrySetConversationIdAsync's CAS in TryPinConversationAsync, so it is not re-written
        // here; passing it separately would reintroduce a read-modify-write clobber window.
        _ = conversationId;
        await _cronStore.RecordRunFinalizationAsync(jobId, triggeredAt, status, error, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks cron runs that are still stamped <see cref="CronRunStatus.Running"/> but whose
    /// <c>StartedAt</c> deviates from now by more than
    /// <see cref="CronOptions.OrphanedRunThresholdSeconds"/> as <see cref="CronRunStatus.Error"/>
    /// with the orphan reason (#2410).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The write-path protection (<c>RecordAbortedRunAsync</c> on every abort path) only covers
    /// graceful aborts. A process kill, host crash, OOM, or power loss leaves the row stamped
    /// <c>running</c> forever - and because retention never deletes in-flight runs, such a row is
    /// also permanently immune to <see cref="ICronStore.PurgeRunsOlderThanAsync"/> and accumulates.
    /// Reaping converts it into a terminal, visible, prunable row.
    /// </para>
    /// <para>
    /// The bound is deliberately compared as <c>Math.Abs(now - startedAt)</c>, not
    /// <c>now - startedAt</c>. A future-dated <c>started_at</c> (clock skew, a restored database, a
    /// forced run) yields a negative span, which a one-sided comparison never exceeds - that row
    /// would be a permanent blind spot and could never be reaped. The symmetric comparison closes it.
    /// </para>
    /// </remarks>
    /// <returns>The number of runs reaped.</returns>
    internal async Task<int> ReapOrphanedRunsAsync(CancellationToken ct = default)
    {
        var options = _optionsMonitor.CurrentValue ?? new CronOptions();
        var bound = TimeSpan.FromSeconds(Math.Max(1, options.OrphanedRunThresholdSeconds));

        var running = await _cronStore.ListRunningRunsAsync(ct).ConfigureAwait(false);
        if (running.Count == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var reaped = 0;

        foreach (var run in running)
        {
            // Symmetric bound: both a long-stale and a future-dated started_at are orphans.
            if ((now - run.StartedAt).Duration() <= bound)
            {
                continue;
            }

            await _cronStore.RecordRunCompleteAsync(run.Id, CronRunStatus.Error, OrphanedRunReason, ct: ct)
                .ConfigureAwait(false);

            // Only clear the job's bookkeeping when it is still advertising this stuck run. A newer
            // terminal run must not be regressed by reaping an older orphan.
            var job = await _cronStore.GetAsync(run.JobId, ct).ConfigureAwait(false);
            if (job is not null && string.Equals(job.LastRunStatus, CronRunStatus.Running, StringComparison.Ordinal))
            {
                await _cronStore.RecordRunFinalizationAsync(
                    run.JobId,
                    run.StartedAt,
                    CronRunStatus.Error,
                    OrphanedRunReason,
                    ct).ConfigureAwait(false);
            }

            reaped++;
            _logger.LogWarning(
                "Reaped orphaned cron run '{RunId}' for job '{JobId}' (started_at {StartedAt:o}, bound {Bound}).",
                run.Id, run.JobId, run.StartedAt, bound);
        }

        return reaped;
    }

    /// <summary>
    /// Records a cron run that ended without completing its action as terminal, so it is never left
    /// stuck <see cref="CronRunStatus.Running"/> (a silent non-success).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is the host-abort shape (gateway shutdown, scheduler stop, an explicit cancel of
    /// a manual run), recorded as <see cref="CronRunStatus.Error"/>. #3160 parameterises the status
    /// and reason so an <b>operator</b> abort - the job was deleted or disabled mid-run - can be
    /// recorded as <see cref="CronRunStatus.Aborted"/> through the same seam, rather than growing a
    /// second near-identical recording path that would inevitably drift.
    /// </para>
    /// <para>
    /// The bookkeeping writes use <see cref="CancellationToken.None"/> because the caller's token is
    /// already cancelled - passing it would cancel the very writes that record the outcome.
    /// </para>
    /// </remarks>
    private async Task RecordAbortedRunAsync(
        RunId runId,
        CronJob job,
        DateTimeOffset triggeredAt,
        string status = CronRunStatus.Error,
        string? reason = null,
        CronRunCost? cost = null)
    {
        const string hostAbortReason = "Cron run aborted before completion.";
        var abortReason = reason ?? hostAbortReason;
        // #2641 AC1: an aborted run is terminal and still records what it cost before the abort.
        await _cronStore.RecordRunCompleteAsync(runId, status, abortReason, cost: cost, ct: CancellationToken.None).ConfigureAwait(false);
        await FinalizeRunAsync(job.Id, job, triggeredAt, status, abortReason, ct: CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Opt-in ephemeral-run cleanup (#1561). When <see cref="CronJob.DeleteAfterRun"/> is set and the
    /// run produced a cron-scoped (<c>cron:</c>) session, deletes that session and its transcript so
    /// run-scoped cron sessions cannot accumulate transcript entries indefinitely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called from a <c>finally</c> that wraps the action execution + completion bookkeeping, so it
    /// runs exactly once across every terminal path (ok / timed_out / aborted / error). Deletion is
    /// best-effort: a failure here is logged and swallowed so it can never mask the run's real outcome
    /// or escape the finally.
    /// </para>
    /// <para>
    /// Guards that make this safe to leave off the hot path for normal jobs:
    /// </para>
    /// <list type="bullet">
    ///   <item>No-op unless the job opted in (<see cref="CronJob.DeleteAfterRun"/>).</item>
    ///   <item>No-op when the action recorded no session id (nothing produced to delete).</item>
    ///   <item>Only deletes sessions whose id begins with <c>cron:</c> — a misconfigured flag on a
    ///   job whose action reuses a long-lived/per-agent session cannot remove that session.</item>
    /// </list>
    /// <para>
    /// Uses <see cref="CancellationToken.None"/> for the delete: when the run was aborted via host
    /// shutdown the caller's token is already cancelled, and we still want the ephemeral session
    /// reclaimed rather than leaked.
    /// </para>
    /// </remarks>
    private async Task MaybeDeleteEphemeralRunSessionAsync(
        CronJob job,
        CronExecutionContext context,
        IServiceProvider services)
    {
        if (!job.DeleteAfterRun)
            return;

        if (context.SessionId is not { } sessionId)
            return;

        // Only ephemeral cron-scoped sessions are eligible — never a long-lived/per-agent session
        // that an action happened to reuse. Mirrors the `cron:` prefix convention used by the
        // legacy-conversation migration sweep.
        if (!sessionId.Value.StartsWith("cron:", StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "DeleteAfterRun set for job '{JobId}' but run session '{SessionId}' is not a cron-scoped session; skipping cleanup.",
                job.Id,
                sessionId.Value);
            return;
        }

        try
        {
            var sessions = services.GetRequiredService<ISessionStore>();
            await sessions.DeleteAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation(
                "Deleted ephemeral cron run session '{SessionId}' (and its transcript) for job '{JobId}' after run (deleteAfterRun).",
                sessionId.Value,
                job.Id);
        }
        catch (Exception ex)
        {
            // Best-effort: a cleanup failure must never mask the run outcome or escape the finally.
            _logger.LogWarning(
                ex,
                "Failed to delete ephemeral cron run session '{SessionId}' for job '{JobId}' after run. The run outcome is unaffected.",
                sessionId.Value,
                job.Id);
        }
    }

    /// <summary>
    /// Recovery path for the multi-process race where two scheduler processes both created
    /// a fresh conversation for the same job's first run and only one CAS won. We rebind the
    /// session created in this run to the winner and archive our loser conversation.
    /// </summary>
    private async Task ReconcileCasLoserAsync(
        IServiceProvider services,
        ConversationId loserConversationId,
        ConversationId winnerConversationId,
        SessionId? sessionId,
        CancellationToken ct)
    {
        _logger.LogWarning(
            "CronScheduler: CAS lost for conversation pinback (loser={Loser}, winner={Winner}). Rebinding session and archiving loser.",
            loserConversationId,
            winnerConversationId);

        var conversations = services.GetRequiredService<IConversationStore>();
        var sessions = services.GetRequiredService<ISessionStore>();

        // Rebind the session we just created to the winning conversation.
        if (sessionId.HasValue)
        {
            var session = await sessions.GetAsync(sessionId.Value, ct).ConfigureAwait(false);
            if (session is not null)
            {
                session.ConversationId = winnerConversationId;
                await sessions.SaveAsync(session, ct).ConfigureAwait(false);
            }
        }

        // Pin the winner's ActiveSessionId so portal renders our latest run.
        var winnerConversation = await conversations.GetAsync(winnerConversationId, ct).ConfigureAwait(false);
        if (winnerConversation is not null && sessionId.HasValue
            && winnerConversation.ActiveSessionId != sessionId.Value)
        {
            winnerConversation.ActiveSessionId = sessionId.Value;
            winnerConversation.UpdatedAt = DateTimeOffset.UtcNow;
            if (winnerConversation.Status == ConversationStatus.Archived)
                winnerConversation.Status = ConversationStatus.Active;
            await conversations.SaveAsync(winnerConversation, ct).ConfigureAwait(false);
        }

        await conversations.ArchiveAsync(loserConversationId, "cron-transient-cleanup", sessionId?.Value, "system", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One-shot startup migration that reconciles cron sessions left orphaned by the
    /// pre-P9-D composite-id conversation model. For each job, the canonical conversation
    /// is chosen (in priority order):
    /// <list type="number">
    ///   <item>The pinned <see cref="CronJob.ConversationId"/> if already set.</item>
    ///   <item>The legacy composite id <c>cronconv:&lt;agent&gt;:&lt;job&gt;</c>.</item>
    ///   <item>An active conversation titled <c>cron:&lt;jobId&gt;</c>.</item>
    ///   <item>An active conversation whose title matches the job's display name.</item>
    /// </list>
    /// Any chosen conversation is pinned onto the job via CAS. Sessions whose
    /// <see cref="SessionId"/> begins with <c>cron:</c> for this agent are rebound onto the
    /// canonical conversation (skipping the canonical itself), and duplicate cron
    /// conversations are archived.
    /// </summary>
    /// <remarks>
    /// Guarded by <see cref="_migrationRan"/> so it runs at most once per process. Idempotent.
    /// The sweep is best-effort per job — failures are logged and migration continues for
    /// subsequent jobs so a single broken job cannot block the scheduler from starting.
    /// </remarks>
    internal async Task MigrateLegacyCronConversationsAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _migrationRan, 1) == 1)
            return;

        await _cronStore.InitializeAsync(ct).ConfigureAwait(false);
        var jobs = await _cronStore.ListAsync(ct: ct).ConfigureAwait(false);
        if (jobs.Count == 0)
            return;

        using var scope = _scopeFactory.CreateScope();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionStore>();

        var migratedJobCount = 0;
        var rebondedSessionCount = 0;
        var archivedConversationCount = 0;

        foreach (var job in jobs)
        {
            if (ct.IsCancellationRequested) break;
            if (job.AgentId is not { } agentId) continue;

            try
            {
                var (canonical, archivedHere, reboundHere) = await ReconcileJobLegacyConversationsAsync(
                    job, agentId, conversations, sessions, ct).ConfigureAwait(false);

                if (canonical is not null)
                {
                    migratedJobCount++;
                    rebondedSessionCount += reboundHere;
                    archivedConversationCount += archivedHere;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex,
                    "Legacy cron migration failed for job '{JobId}'. Continuing with remaining jobs.",
                    job.Id);
            }
        }

        if (migratedJobCount > 0)
        {
            _logger.LogInformation(
                "Legacy cron conversation migration complete. Jobs migrated: {Jobs}, sessions rebound: {Sessions}, duplicate conversations archived: {Archived}.",
                migratedJobCount,
                rebondedSessionCount,
                archivedConversationCount);
        }
    }

    private async Task<(Conversation? Canonical, int Archived, int Rebound)> ReconcileJobLegacyConversationsAsync(
        CronJob job,
        AgentId agentId,
        IConversationStore conversations,
        ISessionStore sessions,
        CancellationToken ct)
    {
        var candidates = await conversations.ListAsync(agentId, ct).ConfigureAwait(false);
        Conversation? canonical = null;

        // Priority 1: already pinned on the job.
        if (job.ConversationId.HasValue)
            canonical = await conversations.GetAsync(job.ConversationId.Value, ct).ConfigureAwait(false);

        // Priority 2: legacy composite id.
        if (canonical is null)
        {
            var legacyCompositeId = ConversationId.From($"cronconv:{Sanitize(agentId.Value)}:{Sanitize(job.Id.Value)}");
            canonical = await conversations.GetAsync(legacyCompositeId, ct).ConfigureAwait(false);
        }

        // Priority 3: title `cron:{jobId}`.
        canonical ??= candidates
            .Where(c => string.Equals(c.Title, $"cron:{Sanitize(job.Id.Value)}", StringComparison.Ordinal))
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefault();

        // Priority 4: title matches the job's display name.
        canonical ??= candidates
            .Where(c => !string.IsNullOrEmpty(c.Title) && string.Equals(c.Title, job.Name, StringComparison.Ordinal))
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefault();

        if (canonical is null)
            return (null, 0, 0);

        if (canonical.Status == ConversationStatus.Archived)
        {
            canonical.Status = ConversationStatus.Active;
            canonical.UpdatedAt = DateTimeOffset.UtcNow;
            await conversations.SaveAsync(canonical, ct).ConfigureAwait(false);
        }

        // Pin via CAS (no-op if already pinned to canonical).
        if (!job.ConversationId.HasValue || job.ConversationId.Value != canonical.ConversationId)
            await _cronStore.TrySetConversationIdAsync(job.Id, canonical.ConversationId, ct).ConfigureAwait(false);

        // Rebind every cron:* session of this agent that points at any conversation other than canonical.
        // Per blocker B4: scan by SessionId.StartsWith("cron:") regardless of current ConversationId,
        // so we handle sessions that P9-B-2 backfill already bound to a per-agent legacy:* conversation.
        var allSessions = await sessions.ListAsync(agentId, ct).ConfigureAwait(false);
        var jobIdSlug = Sanitize(job.Id.Value);
        var reboundCount = 0;
        foreach (var session in allSessions)
        {
            if (!session.SessionId.Value.StartsWith("cron:", StringComparison.Ordinal))
                continue;

            // Match this job only — sessions encode jobId as `cron:{jobIdSlug}:...`. Sessions without
            // a jobId slug (legacy `cron:{ts}:{guid}`) cannot be safely attributed, so we skip them.
            if (!session.SessionId.Value.StartsWith($"cron:{jobIdSlug}:", StringComparison.Ordinal))
                continue;

            if (session.ConversationId.IsInitialized() && session.ConversationId == canonical.ConversationId)
                continue;

            session.ConversationId = canonical.ConversationId;
            await sessions.SaveAsync(session, ct).ConfigureAwait(false);
            reboundCount++;
        }

        // Archive duplicate cron conversations for this agent that share the canonical title.
        var archivedCount = 0;
        var duplicates = candidates
            .Where(c => c.ConversationId != canonical.ConversationId)
            .Where(c => c.Status == ConversationStatus.Active)
            .Where(c =>
                string.Equals(c.Title, canonical.Title, StringComparison.Ordinal)
                || c.ConversationId.Value.StartsWith($"cronconv:{Sanitize(agentId.Value)}:{jobIdSlug}", StringComparison.Ordinal))
            .ToList();

        foreach (var duplicate in duplicates)
        {
            await conversations.ArchiveAsync(duplicate.ConversationId, "cron-duplicate-cleanup", jobIdSlug, "system", ct).ConfigureAwait(false);
            archivedCount++;
        }

        return (canonical, archivedCount, reboundCount);
    }

    private static string Sanitize(string value)
    {
        Span<char> buffer = stackalloc char[Math.Min(40, value.Length)];
        var length = 0;
        foreach (var ch in value)
        {
            if (length >= buffer.Length) break;
            buffer[length++] = (char.IsLetterOrDigit(ch) || ch is '-' or '_') ? ch : '-';
        }
        return new string(buffer[..length]).Trim('-');
    }

    private ICronAction ResolveAction(string actionType)
    {
        if (_actions.TryGetValue(actionType, out var action))
            return action;

        throw new InvalidOperationException($"No cron action registered for type '{actionType}'.");
    }

    private bool TryGetSchedule(CronJob job, out CronExpression expression)
    {
        try
        {
            expression = CronExpression.Parse(job.Schedule, CronFormat.Standard);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invalid cron expression for job {JobId}: {Schedule}", job.Id, job.Schedule);
            expression = default!;
            return false;
        }
    }

    // #2748: resolution has exactly ONE definition (CronTimeZoneResolver). The scheduler used to
    // carry its own weaker copy - a single FindSystemTimeZoneById with no Windows/IANA translation
    // and no InvalidTimeZoneException handling - which made the next-run computation disagree with
    // the actions that ran the job. Do NOT reintroduce a local wrapper here: call the resolver
    // directly and pass _logger so a degradation to UTC is reported rather than swallowed.
    // Guarded by CronSchedulerTimeZoneResolutionTests.

    private int? ResolveJobTimeout(CronJob job)
    {
        var options = _optionsMonitor.CurrentValue ?? new CronOptions();
        var fallback = options.DefaultJobTimeoutSeconds > 0 ? options.DefaultJobTimeoutSeconds : 3600;

        // #2904: shape handling, the 0-means-unlimited sentinel and the invalid-value warning live
        // in CronTimeoutResolver so this site and CommandCronAction cannot answer the same metadata
        // value differently - they already had, the command action never accepted double/JsonElement.
        return CronTimeoutResolver.Resolve(job, fallback, _logger);
    }

    /// <summary>
    /// Config-sourced half of the #2671 alert-target gate. Routes through the SAME
    /// <see cref="CronAlertTarget.ValidateAsync"/> as the API seam so the three authoring paths
    /// cannot answer "is this target reachable?" differently, but downgrades the outcome from
    /// rejection to a warning: config jobs load at boot, where there is no operator to correct a
    /// payload and a hard failure would take the whole scheduler down.
    /// </summary>
    /// <param name="jobIdString">Configured job id, named in the warning so it is actionable.</param>
    /// <param name="configuredTarget">Raw configured target, or null/blank when alerting is off.</param>
    /// <param name="ct">The cancellation token.</param>
    private async Task WarnIfConfiguredAlertTargetUnresolvableAsync(
        string jobIdString,
        string? configuredTarget,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configuredTarget))
            return;

        using var scope = _scopeFactory.CreateScope();
        var resolver = scope.ServiceProvider.GetService<ICronAlertTargetResolver>();
        var validation = await CronAlertTarget
            .ValidateAsync(resolver, ConversationId.From(configuredTarget), ct)
            .ConfigureAwait(false);
        if (validation.IsValid)
            return;

        _logger.LogWarning(
            "Configured cron job '{JobId}' has an unresolvable failureAlertConversationId '{ConversationId}'. "
            + "The job still loads, but its failure alerts cannot be delivered. {Reason}",
            jobIdString,
            configuredTarget,
            validation.Error);
    }

    private async Task SyncConfiguredJobsAsync(CronOptions options, CancellationToken ct)
    {
        if (options.Jobs is null || options.Jobs.Count == 0)
            return;

        foreach (var (jobIdString, configuredJob) in options.Jobs)
        {
            if (string.IsNullOrWhiteSpace(jobIdString) ||
                string.IsNullOrWhiteSpace(configuredJob.Schedule) ||
                string.IsNullOrWhiteSpace(configuredJob.ActionType))
            {
                _logger.LogWarning(
                    "Skipping configured cron job '{JobId}' due to missing required fields (schedule/actionType).",
                    jobIdString);
                continue;
            }

            var normalizedActionType = NormalizeActionType(configuredJob.ActionType);
            if (!_actions.ContainsKey(normalizedActionType))
            {
                _logger.LogWarning(
                    "Skipping configured cron job '{JobId}' because action type '{ActionType}' is not registered.",
                    jobIdString,
                    configuredJob.ActionType);
                continue;
            }

            if (string.Equals(normalizedActionType, "agent-prompt", StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(configuredJob.AgentId)
                    || (string.IsNullOrWhiteSpace(configuredJob.Message) && string.IsNullOrWhiteSpace(configuredJob.TemplateName))))
            {
                _logger.LogWarning(
                    "Skipping configured cron job '{JobId}' because agent-prompt jobs require agentId and either message or templateName.",
                    jobIdString);
                continue;
            }

            // #2552: the declarative surface goes through the same shared boundary as the API so
            // the two cannot drift. A config-declared job with a credential-bearing or non-http(s)
            // webhook URL is skipped loudly rather than materialised into the store.
            if (!CronWebhookUrl.TryNormalize(configuredJob.WebhookUrl, out var normalizedWebhookUrl, out var webhookRejectionReason))
            {
                // #2745: log the rule-specific reason so an operator can tell a blocked address
                // class apart from a scheme/credentials rejection without reading the source.
                _logger.LogWarning(
                    "Skipping configured cron job '{JobId}' because its webhookUrl is invalid. {Reason}",
                    jobIdString,
                    webhookRejectionReason);
                continue;
            }

            var jobId = JobId.From(jobIdString);

            // #2671 clause 5: a config-declared job whose failure-alert target does not resolve is
            // WARNED about and still loaded. Refusing to boot the scheduler because one job's alert
            // target went stale would be a strictly worse failure than the one being fixed - the
            // deliberate asymmetry with the API seam, which rejects because a human is present to
            // read the error and correct the payload.
            await WarnIfConfiguredAlertTargetUnresolvableAsync(jobIdString, configuredJob.FailureAlertConversationId, ct)
                .ConfigureAwait(false);

            var agentId = string.IsNullOrWhiteSpace(configuredJob.AgentId)
                ? (AgentId?)null
                : AgentId.From(configuredJob.AgentId);

            var existing = await _cronStore.GetAsync(jobId, ct).ConfigureAwait(false);
            if (existing is null)
            {
                var seeded = new CronJob
                {
                    Id = jobId,
                    Name = configuredJob.Name ?? jobIdString,
                    Schedule = configuredJob.Schedule,
                    ActionType = normalizedActionType,
                    AgentId = agentId,
                    Message = configuredJob.Message,
                    TemplateName = configuredJob.TemplateName,
                    TemplateParameters = configuredJob.TemplateParameters,
                    Model = configuredJob.Model,
                    WebhookUrl = normalizedWebhookUrl,
                    ShellCommand = configuredJob.ShellCommand,
                    Enabled = configuredJob.Enabled,
                    System = configuredJob.System,
                    DeleteAfterRun = configuredJob.DeleteAfterRun,
                    DeleteJobAfterRun = configuredJob.DeleteJobAfterRun,
                    ExpiresAt = ParseConfiguredExpiry(configuredJob.ExpiresAt, jobId),
                    ExecutionClass = configuredJob.ExecutionClass,
                FailureAlertsEnabled = configuredJob.FailureAlertsEnabled,
                FailureAlertConversationId = string.IsNullOrWhiteSpace(configuredJob.FailureAlertConversationId)
                    ? null
                    : ConversationId.From(configuredJob.FailureAlertConversationId),
                    TimeZone = configuredJob.TimeZone,
                    CreatedBy = configuredJob.CreatedBy,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Metadata = configuredJob.Metadata
                };
                await _cronStore.CreateAsync(seeded, ct).ConfigureAwait(false);
                continue;
            }

            var merged = existing with
            {
                Name = configuredJob.Name ?? existing.Name,
                Schedule = configuredJob.Schedule,
                ActionType = normalizedActionType,
                AgentId = agentId,
                Message = configuredJob.Message,
                TemplateName = configuredJob.TemplateName,
                TemplateParameters = configuredJob.TemplateParameters,
                Model = configuredJob.Model,
                WebhookUrl = normalizedWebhookUrl,
                ShellCommand = configuredJob.ShellCommand,
                Enabled = configuredJob.Enabled,
                System = configuredJob.System,
                DeleteAfterRun = configuredJob.DeleteAfterRun,
                DeleteJobAfterRun = configuredJob.DeleteJobAfterRun,
                ExpiresAt = ParseConfiguredExpiry(configuredJob.ExpiresAt, jobId),
                ExecutionClass = configuredJob.ExecutionClass,
                FailureAlertsEnabled = configuredJob.FailureAlertsEnabled,
                FailureAlertConversationId = string.IsNullOrWhiteSpace(configuredJob.FailureAlertConversationId)
                    ? null
                    : ConversationId.From(configuredJob.FailureAlertConversationId),
                TimeZone = configuredJob.TimeZone ?? existing.TimeZone,
                CreatedBy = configuredJob.CreatedBy ?? existing.CreatedBy,
                Metadata = configuredJob.Metadata ?? existing.Metadata
            };

            // #2133: config-sync is a definition write only; runtime/conversation columns
            // are scheduler/CAS-owned and must not be round-tripped here.
            await _cronStore.UpdateDefinitionAsync(merged, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Parses a config-declared expiresAt string (#2634). An unparseable value degrades to null
    /// (no expiry) with a warning rather than throwing or guessing: a typo in config must never
    /// silently suppress a job the operator still wants running.
    /// </summary>
    private DateTimeOffset? ParseConfiguredExpiry(string? expiresAt, JobId jobId)
    {
        if (string.IsNullOrWhiteSpace(expiresAt))
            return null;

        if (DateTimeOffset.TryParse(
                expiresAt,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return parsed;
        }

        _logger.LogWarning(
            "Configured cron job '{JobId}' has an unparseable expiresAt value '{ExpiresAt}'; treating it as no expiry.",
            jobId,
            expiresAt);
        return null;
    }

    private static string NormalizeActionType(string? actionType)
    {
        if (string.Equals(actionType, "agent-chat", StringComparison.OrdinalIgnoreCase))
            return "agent-prompt";

        return actionType?.Trim() ?? string.Empty;
    }
}

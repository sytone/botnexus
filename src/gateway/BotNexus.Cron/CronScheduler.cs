using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
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
    ILogger<CronScheduler> logger) : BackgroundService
{
    private readonly ICronStore _cronStore = cronStore;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IOptionsMonitor<CronOptions> _optionsMonitor = optionsMonitor;
    private readonly ILogger<CronScheduler> _logger = logger;
    private readonly IReadOnlyDictionary<string, ICronAction> _actions = actions
        .GroupBy(action => action.ActionType, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

    // Per-job in-process lock guards the "create conversation -> CAS stamp" critical section so
    // concurrent runs of the SAME job in this process cannot both create their own conversation.
    // Multi-process races (e.g. CLI `cron run` while the gateway scheduler also fires) are still
    // possible but are cleaned up by the next scheduler-startup migration sweep.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _jobLocks = new(StringComparer.Ordinal);

    // One-shot legacy migration guard. The scheduler runs the legacy-conversation migration
    // exactly once per process lifetime, gated by this flag.
    private int _migrationRan;

    public async Task<CronRun> RunNowAsync(JobId jobId, CancellationToken cancellationToken = default)
    {
        await _cronStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var job = await _cronStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Cron job '{jobId}' was not found.");

        return await RunActionAsync(job, CronTriggerType.Manual, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a cron job and archives its associated conversation, if any.
    /// Per directive G-5, the conversation lives "until deleted" — deleting the job is
    /// the canonical signal to archive the conversation thread.
    /// </summary>
    /// <remarks>
    /// Idempotent: missing jobs and missing conversations are not errors. The conversation
    /// is archived first (best-effort) before the job row is removed, so a failure to
    /// archive surfaces an error and leaves the job intact for retry.
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

        if (existing.ConversationId.HasValue)
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

        await _cronStore.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
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
        var jobs = await _cronStore.ListAsync(ct: ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        // Phase 1 (sequential): resolve NextRunAt for uninitialised or stale jobs.
        // These are cheap store-only operations with no agent I/O.
        var dueJobs = new List<(CronJob Job, CronExpression Expression)>();
        foreach (var job in jobs.Where(j => j.Enabled))
        {
            if (!TryGetSchedule(job, out var expression))
                continue;

            var tz = ResolveTimeZone(job);
            var computedNext = expression.GetNextOccurrence(now, tz);

            if (job.NextRunAt is null)
            {
                var initialized = job with { NextRunAt = computedNext };
                await _cronStore.UpdateAsync(initialized, ct).ConfigureAwait(false);
                continue;
            }

            // Detect stale NextRunAt: if the schedule was changed to fire sooner
            // than the stored value, correct it so the job isn't stuck waiting on
            // a NextRunAt that no longer matches the current schedule.
            if (computedNext is not null && computedNext < job.NextRunAt)
            {
                var corrected = job with { NextRunAt = computedNext };
                await _cronStore.UpdateAsync(corrected, ct).ConfigureAwait(false);
                if (computedNext > now)
                    continue;
            }

            if (job.NextRunAt > now)
                continue;

            dueJobs.Add((job, expression));
        }

        if (dueJobs.Count == 0)
            return;

        // Phase 2 (concurrent): execute all due jobs in parallel so a long-running
        // agent prompt for one job does not delay other due jobs or user-facing sessions.
        var runTasks = dueJobs.Select(async entry =>
        {
            var (job, expression) = entry;
            var tz = ResolveTimeZone(job);
            await RunActionAsync(job, CronTriggerType.Scheduled, now, ct).ConfigureAwait(false);

            var latest = await _cronStore.GetAsync(job.Id, ct).ConfigureAwait(false) ?? job;
            var updated = latest with { NextRunAt = expression.GetNextOccurrence(now, tz) };
            await _cronStore.UpdateAsync(updated, ct).ConfigureAwait(false);
        });

        await Task.WhenAll(runTasks).ConfigureAwait(false);
    }

    private async Task<CronRun> RunActionAsync(CronJob job, CronTriggerType triggerType, DateTimeOffset triggeredAt, CancellationToken ct)
    {
        var run = await _cronStore.RecordRunStartAsync(job.Id, ct).ConfigureAwait(false);
        var action = ResolveAction(NormalizeActionType(job.ActionType));

        // Serialize same-job runs in this process so the "create conversation -> CAS stamp"
        // window is single-threaded; concurrent triggers for OTHER jobs run unimpeded.
        var jobLock = _jobLocks.GetOrAdd(job.Id.Value, _ => new SemaphoreSlim(1, 1));
        try
        {
            await jobLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Aborted while waiting for the per-job lock (another same-job run held it during a
            // shutdown/cancel). The run was already stamped Running by RecordRunStartAsync above,
            // so record the abort here too - otherwise it stays stuck Running. CancellationToken.None
            // for the write since `ct` is cancelled.
            await RecordAbortedRunAsync(run.Id, job, triggeredAt).ConfigureAwait(false);
            throw;
        }

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
                var timeoutError = await ExecuteActionWithTimeoutAsync(action, context, timeoutSeconds, ct)
                    .ConfigureAwait(false);

                if (timeoutError is not null)
                {
                    await _cronStore.RecordRunCompleteAsync(run.Id, CronRunStatus.TimedOut, timeoutError, ct: ct)
                        .ConfigureAwait(false);
                    await FinalizeRunAsync(job.Id, jobForRun, triggeredAt, CronRunStatus.TimedOut, timeoutError, ct: ct)
                        .ConfigureAwait(false);
                    return run with { Status = CronRunStatus.TimedOut, CompletedAt = DateTimeOffset.UtcNow, Error = timeoutError };
                }

                _logger.LogInformation("Cron job executed: {JobName} ({JobId}) action={ActionType} trigger={TriggerType}",
                    jobForRun.Name, jobForRun.Id, jobForRun.ActionType, triggerType);
                await _cronStore.RecordRunCompleteAsync(run.Id, CronRunStatus.Ok, sessionId: context.SessionId, ct: ct).ConfigureAwait(false);

                // Pinback via CAS: if the trigger created a new conversation for this run and the job
                // has no pinned ConversationId yet, atomically stamp ours onto the job. If another
                // run won the race (multi-process), archive ours and rebind our session to the winner.
                var winningConversationId = await TryPinConversationAsync(job.Id, jobForRun, context, scope.ServiceProvider, ct)
                    .ConfigureAwait(false);

                await FinalizeRunAsync(job.Id, jobForRun, triggeredAt, CronRunStatus.Ok, error: null,
                    conversationId: winningConversationId, ct: ct).ConfigureAwait(false);

                return run with { Status = CronRunStatus.Ok, CompletedAt = DateTimeOffset.UtcNow, SessionId = context.SessionId };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The run was aborted via the host token (gateway shutdown, scheduler stop, or an
                // explicit cancel of a manual run) rather than the per-job timeout. Without this
                // branch the cancellation would leave the run permanently in the Running state it
                // was stamped with at RecordRunStartAsync - a silent non-success that masquerades as
                // never having finished. Record it as a failed run so the abort is visible, then
                // rethrow to preserve cancellation semantics for the caller/host shutdown.
                _logger.LogWarning(
                    "Cron job aborted (cancellation requested). JobId: {JobId}, ActionType: {ActionType}",
                    job.Id, job.ActionType);
                await RecordAbortedRunAsync(run.Id, job, triggeredAt).ConfigureAwait(false);
                throw;
            }
            finally
            {
                await MaybeDeleteEphemeralRunSessionAsync(jobForRun, context, scope.ServiceProvider).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Cron job execution failed. JobId: {JobId}, ActionType: {ActionType}", job.Id, job.ActionType);
            await _cronStore.RecordRunCompleteAsync(run.Id, CronRunStatus.Error, ex.Message, ct: ct).ConfigureAwait(false);
            await FinalizeRunAsync(job.Id, job, triggeredAt, CronRunStatus.Error, ex.ToString(), ct: ct).ConfigureAwait(false);
            return run with { Status = CronRunStatus.Error, CompletedAt = DateTimeOffset.UtcNow, Error = ex.Message };
        }
        finally
        {
            jobLock.Release();
        }
    }

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
        int timeoutSeconds,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

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
    /// The single "re-read latest job → <see cref="ICronStore.UpdateAsync"/> with the terminal
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
        var latest = await _cronStore.GetAsync(jobId, ct).ConfigureAwait(false) ?? fallback;
        await _cronStore.UpdateAsync(latest with
        {
            LastRunAt = triggeredAt,
            LastRunStatus = status,
            LastRunError = error,
            ConversationId = conversationId ?? latest.ConversationId
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a cron run that was aborted via the host cancellation token (gateway shutdown,
    /// scheduler stop, or an explicit cancel) as a failed run. The run was already stamped
    /// <see cref="CronRunStatus.Running"/> by <see cref="ICronStore.RecordRunStartAsync"/>, so this
    /// surfaces the abort as <see cref="CronRunStatus.Error"/> instead of leaving it stuck
    /// <see cref="CronRunStatus.Running"/> forever (a silent non-success). The bookkeeping writes use
    /// <see cref="CancellationToken.None"/> because the caller's token is already cancelled - passing
    /// it would cancel the very writes that record the failure.
    /// </summary>
    private async Task RecordAbortedRunAsync(RunId runId, CronJob job, DateTimeOffset triggeredAt)
    {
        const string abortReason = "Cron run aborted before completion.";
        await _cronStore.RecordRunCompleteAsync(runId, CronRunStatus.Error, abortReason, ct: CancellationToken.None).ConfigureAwait(false);
        await FinalizeRunAsync(job.Id, job, triggeredAt, CronRunStatus.Error, abortReason, ct: CancellationToken.None).ConfigureAwait(false);
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

    private static TimeZoneInfo ResolveTimeZone(CronJob job)
    {
        if (string.IsNullOrWhiteSpace(job.TimeZone))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(job.TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private int ResolveJobTimeout(CronJob job)
    {
        if (job.Metadata is not null &&
            job.Metadata.TryGetValue("timeoutSeconds", out var raw) &&
            raw is not null)
        {
            if (raw is int i && i > 0) return i;
            if (raw is long l && l > 0) return (int)Math.Min(l, int.MaxValue);
            if (raw is double d && d > 0) return (int)d;
            if (raw is System.Text.Json.JsonElement je && je.TryGetInt32(out var parsed) && parsed > 0) return parsed;
            if (raw is string s && int.TryParse(s, out var ps) && ps > 0) return ps;
        }

        var options = _optionsMonitor.CurrentValue ?? new CronOptions();
        return options.DefaultJobTimeoutSeconds > 0 ? options.DefaultJobTimeoutSeconds : 3600;
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

            var jobId = JobId.From(jobIdString);
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
                    WebhookUrl = configuredJob.WebhookUrl,
                    ShellCommand = configuredJob.ShellCommand,
                    Enabled = configuredJob.Enabled,
                    System = configuredJob.System,
                    DeleteAfterRun = configuredJob.DeleteAfterRun,
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
                WebhookUrl = configuredJob.WebhookUrl,
                ShellCommand = configuredJob.ShellCommand,
                Enabled = configuredJob.Enabled,
                System = configuredJob.System,
                DeleteAfterRun = configuredJob.DeleteAfterRun,
                TimeZone = configuredJob.TimeZone ?? existing.TimeZone,
                CreatedBy = configuredJob.CreatedBy ?? existing.CreatedBy,
                Metadata = configuredJob.Metadata ?? existing.Metadata
            };

            await _cronStore.UpdateAsync(merged, ct).ConfigureAwait(false);
        }
    }

    private static string NormalizeActionType(string? actionType)
    {
        if (string.Equals(actionType, "agent-chat", StringComparison.OrdinalIgnoreCase))
            return "agent-prompt";

        return actionType?.Trim() ?? string.Empty;
    }
}

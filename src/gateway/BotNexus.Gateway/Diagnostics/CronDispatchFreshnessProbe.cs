using BotNexus.Cron;
using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Result of a dispatch-freshness check: the jobs that were due but have not been dispatched
/// within the grace window, and how overdue the oldest one is.
/// </summary>
public sealed record DispatchFreshnessResult(
    TimeSpan OldestOverdue,
    IReadOnlyList<string> OverdueJobIds,
    bool StoreUnreadable = false,
    int PendingConfirmation = 0)
{
    /// <summary>No job is overdue: the scheduler is either dispatching or has nothing due.</summary>
    public static DispatchFreshnessResult Healthy { get; } = new(TimeSpan.Zero, []);

    /// <summary>
    /// The cron store could not be read, so freshness is UNKNOWN. This fails closed (#4732): an
    /// unreadable store hides a stall exactly like #4689, so it is not evidence of health.
    /// </summary>
    public static DispatchFreshnessResult Unreadable { get; } = new(TimeSpan.Zero, [], StoreUnreadable: true);

    /// <summary>
    /// True when at least one due job has not been dispatched within the grace window, or when
    /// dispatch could not be verified because the store was unreadable.
    /// </summary>
    public bool IsStalled => StoreUnreadable || OverdueJobIds.Count > 0;
}

/// <summary>
/// Reports whether scheduled work that is DUE is actually being dispatched (#4689). Unlike the
/// thread-pool probe this is direct evidence: a job whose effective next-run time is well in the
/// past was not fired, regardless of how responsive the process is.
/// </summary>
public interface IDispatchFreshnessProbe
{
    /// <summary>Evaluates dispatch freshness at the current instant.</summary>
    Task<DispatchFreshnessResult> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Dispatch-freshness probe over the cron store. The scheduler advances a job's
/// <c>NextRunAt</c> only after the fired run returns, so a wedged tick loop OR a hung run leaves
/// <c>max(NextRunAt, BackoffUntil)</c> in the past. Jobs with nothing due (future next run, disabled,
/// expired, invalid schedule, or the scheduler switched off) are never reported, so a legitimately
/// idle host stays quiet.
/// </summary>
public sealed class CronDispatchFreshnessProbe : IDispatchFreshnessProbe
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    // #4732 MEDIUM-1: overdue (jobId, due) pairs seen on an earlier probe, with the wall-clock
    // instant of first sighting. A pair is only reported once it is still overdue >= 2 ticks later.
    private readonly Dictionary<(string JobId, DateTimeOffset Due), DateTimeOffset> _candidates = new();
    private DateTimeOffset _startedAtUtc;
    private DateTimeOffset _lastWallUtc;
    private long _lastTimestamp;

    /// <summary>Creates the probe; the cron store is resolved lazily so hosts without cron work.</summary>
    public CronDispatchFreshnessProbe(
        IServiceProvider services,
        TimeProvider? timeProvider = null,
        ILogger<CronDispatchFreshnessProbe>? logger = null)
    {
        _services = services;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _startedAtUtc = _timeProvider.GetUtcNow();
        _lastWallUtc = _startedAtUtc;
        _lastTimestamp = _timeProvider.GetTimestamp();
    }

    /// <inheritdoc />
    public async Task<DispatchFreshnessResult> CheckAsync(CancellationToken cancellationToken)
    {
        var store = _services.GetService<ICronStore>();
        if (store is null)
        {
            return DispatchFreshnessResult.Healthy;
        }

        var options = _services.GetService<IOptionsMonitor<CronOptions>>()?.CurrentValue ?? new CronOptions();
        if (!options.Enabled)
        {
            // Scheduler deliberately off: nothing is expected to dispatch.
            return DispatchFreshnessResult.Healthy;
        }

        var now = _timeProvider.GetUtcNow();
        IReadOnlyList<CronJob> jobs;
        try
        {
            jobs = await store.ListAsync(ct: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // #4732: fail CLOSED. An unreadable store cannot prove due work is dispatching.
            _logger.LogWarning(
                ex,
                "Cron dispatch freshness UNKNOWN: the cron store could not be read; treating dispatch as NOT fresh.");
            return DispatchFreshnessResult.Unreadable;
        }

        // #4732 round-3 MEDIUM-1: an unlimited job is exempt only while a run is actually in flight.
        // Read running runs once per probe, and only when an unlimited job exists.
        HashSet<string>? runningJobIds = null;
        if (jobs.Any(j => GetGrace(options, j) is null))
        {
            try
            {
                var running = await store.ListRunningRunsAsync(cancellationToken).ConfigureAwait(false);
                runningJobIds = running.Select(r => r.JobId.Value).ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "Cron dispatch freshness UNKNOWN: running cron runs could not be read; treating dispatch as NOT fresh.");
                return DispatchFreshnessResult.Unreadable;
            }
        }

        var oldest = TimeSpan.Zero;
        var overdue = new List<string>();
        var pending = 0;
        var confirmWindow = TimeSpan.FromSeconds(2 * Math.Max(1, options.TickIntervalSeconds));
        lock (_gate)
        {
            DetectWallClockJump(now, confirmWindow);
            var seen = new HashSet<(string, DateTimeOffset)>();
            foreach (var job in jobs)
            {
                if (!job.Enabled || job.NextRunAt is not { } nextRun)
                    continue;
                if (job.ExpiresAt is { } expiresAt && expiresAt <= now)
                    continue;
                if (!IsValidSchedule(job.Schedule))
                    continue;

                var stored = job.BackoffUntil is { } floor && floor > nextRun ? floor : nextRun;
                // A job that fell due before this process started (gateway downtime) or before a
                // detected wall-clock jump (host sleep) is measured from that baseline, not from its
                // stale due time: the scheduler has not had a tick to run it yet.
                var due = stored < _startedAtUtc ? _startedAtUtc : stored;

                var lateBy = now - due;
                // #4732 MEDIUM-2: honour the job's own effective timeout. An unlimited job may hold its
                // NextRunAt in the past indefinitely WHILE RUNNING; with no run in flight it falls
                // back to the default grace so a dead tick loop is still reported (round-3 MEDIUM-1).
                TimeSpan grace;
                if (GetGrace(options, job) is { } jobGrace)
                    grace = jobGrace;
                else if (runningJobIds!.Contains(job.Id.Value))
                    continue;
                else
                    grace = GetGrace(options);

                if (lateBy < grace)
                    continue;

                // #4732 MEDIUM-1: one stale observation is only a candidate. Confirm it with the
                // SAME (jobId, due) pair still overdue at least two ticks later, so a resume after
                // host sleep (scheduler about to tick) or a transient race is not a CRITICAL.
                var key = (job.Id.Value, stored);
                seen.Add(key);
                if (!_candidates.TryGetValue(key, out var firstSeen))
                {
                    _candidates[key] = now;
                    pending++;
                    continue;
                }

                if (now - firstSeen < confirmWindow)
                {
                    pending++;
                    continue;
                }

                overdue.Add(job.Id.Value);
                if (lateBy > oldest)
                    oldest = lateBy;
            }

            foreach (var key in _candidates.Keys.Where(k => !seen.Contains(k)).ToList())
                _candidates.Remove(key);
        }

        return overdue.Count == 0
            ? (pending == 0 ? DispatchFreshnessResult.Healthy : DispatchFreshnessResult.Healthy with { PendingConfirmation = pending })
            : new DispatchFreshnessResult(oldest, overdue, PendingConfirmation: pending);
    }

    /// <summary>
    /// A forward wall-clock step that the monotonic clock did not see (NTP step, VM pause, host
    /// suspend on platforms where the timestamp halts) is treated as a fresh baseline, exactly like a
    /// process restart: jobs that fell due during the gap get a chance to tick before being judged.
    /// </summary>
    private void DetectWallClockJump(DateTimeOffset now, TimeSpan slack)
    {
        var timestamp = _timeProvider.GetTimestamp();
        var wallDelta = now - _lastWallUtc;
        var monoDelta = _timeProvider.GetElapsedTime(_lastTimestamp, timestamp);
        _lastWallUtc = now;
        _lastTimestamp = timestamp;

        if (wallDelta - monoDelta > slack)
        {
            _logger.LogInformation(
                "Cron dispatch freshness: wall clock advanced {WallDelta} while only {MonotonicDelta} elapsed " +
                "(host sleep or clock step); resetting the freshness baseline.",
                wallDelta,
                monoDelta);
            _startedAtUtc = now;
            _candidates.Clear();
        }
    }

    /// <summary>
    /// A tick awaits every due run it fans out, so a legitimately long run (bounded by the job
    /// timeout) delays the next-run bookkeeping of the jobs it ran with. The grace therefore covers
    /// the default job timeout plus two ticks, and is never shorter than 30 minutes.
    /// </summary>
    internal static TimeSpan GetGrace(CronOptions options)
        => GraceFor(options.DefaultJobTimeoutSeconds > 0 ? options.DefaultJobTimeoutSeconds : 3600, options);

    /// <summary>
    /// Per-job grace (#4732): <c>max(30m, effectiveTimeout(job) + 2 ticks)</c>, where the effective
    /// timeout comes from the scheduler's own <see cref="CronTimeoutResolver"/>. Returns <c>null</c>
    /// for a job configured as unlimited (<c>timeoutSeconds: 0</c>); the probe exempts such a job only
    /// while it has a running run.
    /// </summary>
    internal static TimeSpan? GetGrace(CronOptions options, CronJob job, ILogger? logger = null)
    {
        var defaultSeconds = options.DefaultJobTimeoutSeconds > 0 ? options.DefaultJobTimeoutSeconds : 3600;
        return CronTimeoutResolver.Resolve(job, defaultSeconds, logger) is { } seconds
            ? GraceFor(seconds, options)
            : null;
    }

    private static TimeSpan GraceFor(int timeoutSeconds, CronOptions options)
    {
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var ticks = TimeSpan.FromSeconds(2 * Math.Max(1, options.TickIntervalSeconds));
        var grace = timeout + ticks;
        return grace < TimeSpan.FromMinutes(30) ? TimeSpan.FromMinutes(30) : grace;
    }

    private static bool IsValidSchedule(string schedule)
    {
        try
        {
            CronExpression.Parse(schedule, CronFormat.Standard);
            return true;
        }
        catch
        {
            // The scheduler skips unparseable schedules too, so they are not dispatch evidence.
            return false;
        }
    }
}

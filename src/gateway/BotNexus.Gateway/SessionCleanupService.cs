using System.Diagnostics;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway;

public sealed class SessionCleanupService(
    ISessionStore sessionStore,
    IOptions<SessionCleanupOptions> optionsAccessor,
    ILogger<SessionCleanupService> logger,
    SessionLifecycleEvents? lifecycleEvents = null,
    ISessionTurnTracker? turnTracker = null) : BackgroundService
{
    private readonly ISessionTurnTracker? _turnTracker = turnTracker;

    private readonly ISessionStore _sessionStore = sessionStore;
    private readonly ILogger<SessionCleanupService> _logger = logger;
    private readonly SessionLifecycleEvents? _lifecycleEvents = lifecycleEvents;
    private SessionCleanupOptions Options => optionsAccessor.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanupOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session cleanup iteration failed.");
            }

            var delay = Options.CheckInterval <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : Options.CheckInterval;
            await Task.Delay(delay, stoppingToken);
        }
    }

    public async Task RunCleanupOnceAsync(CancellationToken cancellationToken = default)
    {
        const int pageSize = 512;
        var options = Options;
        var ttl = options.SessionTtl <= TimeSpan.Zero ? TimeSpan.FromHours(24) : options.SessionTtl;
        var now = DateTimeOffset.UtcNow;
        var includeBytes = options.ResolveMaxDiskBytes() is not null;
        var diskPlanRows = includeBytes ? new List<SessionCleanupPlanRow>() : null;
        string? cursor = null;
        var scannedRows = 0;
        var started = Stopwatch.StartNew();
        var allocatedBefore = GC.GetTotalAllocatedBytes();

        do
        {
            var page = await _sessionStore.ListCleanupPlanAsync(
                pageSize, includeBytes, cursor, cancellationToken).ConfigureAwait(false);
            scannedRows += page.Rows.Count;
            foreach (var row in page.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasInFlightRun(row.SessionId))
                {
                    _logger.LogDebug("Skipping session cleanup for {SessionId}: an agent run is in flight.", row.SessionId.Value);
                    diskPlanRows?.Add(row);
                    continue;
                }

                var fence = SessionCleanupFence.Capture(row);
                if (row.Status == SessionStatus.Active && now - row.UpdatedAt > ttl)
                {
                    var outcome = await _sessionStore.ExpireIfMatchesAsync(fence, now, cancellationToken).ConfigureAwait(false);
                    if (outcome == SessionMutationOutcome.Applied)
                    {
                        diskPlanRows?.Add(row with { Status = SessionStatus.Expired, UpdatedAt = now });
                        if (_lifecycleEvents is not null)
                            await PublishLifecycleAsync(row, SessionLifecycleEventType.Expired, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        diskPlanRows?.Add(row);
                    }
                    continue;
                }

                var deleteForClosedRetention = options.ClosedSessionRetention is { } closedRetention
                    && closedRetention > TimeSpan.Zero && row.Status == SessionStatus.Sealed
                    && now - row.UpdatedAt > closedRetention;
                var deleteForCronRetention = options.CronNoopRetention is { } cronRetention
                    && cronRetention > TimeSpan.Zero && row.SessionId.IsCron && row.MessageCount <= 2
                    && now - row.UpdatedAt > cronRetention;
                if (deleteForClosedRetention || deleteForCronRetention)
                {
                    var outcome = await _sessionStore.DeleteIfMatchesAsync(fence, cancellationToken).ConfigureAwait(false);
                    if (outcome == SessionMutationOutcome.Applied)
                    {
                        if (_lifecycleEvents is not null)
                            await PublishLifecycleAsync(row, SessionLifecycleEventType.Deleted, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                diskPlanRows?.Add(row);
            }

            cursor = page.NextCursor;
        } while (cursor is not null);

        _logger.LogInformation(
            "Session cleanup projection scan complete: {ScannedRows} row(s), {ElapsedMilliseconds} ms, {AllocatedBytes} allocated bytes",
            scannedRows, started.ElapsedMilliseconds, GC.GetTotalAllocatedBytes() - allocatedBefore);

        if (diskPlanRows is not null)
            await ApplyDiskBudgetAsync(diskPlanRows, options, cancellationToken).ConfigureAwait(false);
    }

    private bool HasInFlightRun(SessionId sessionId) =>
        _turnTracker is not null && _turnTracker.HasLiveTurn(sessionId.Value);

    private Task PublishLifecycleAsync(
        SessionCleanupPlanRow row, SessionLifecycleEventType type, CancellationToken cancellationToken) =>
        _lifecycleEvents!.PublishAsync(
            new SessionLifecycleEvent(row.SessionId.Value, row.AgentId.Value, type, null),
            cancellationToken);

    /// <summary>
    /// Applies the optional session-directory disk budget (issue #2848) at the end of the same
    /// cleanup cycle, so no second timer is introduced. Sessions are grouped per agent because the
    /// budget is a per-agent directory budget, matching how sessions are laid out on disk.
    /// </summary>
    /// <remarks>
    /// Runs AFTER the age predicates deliberately: whatever TTL/retention already reclaimed is not
    /// counted as pressure, so a correctly-configured age policy keeps the size path dormant.
    /// </remarks>
    private async Task ApplyDiskBudgetAsync(
        IReadOnlyList<SessionCleanupPlanRow> rows,
        SessionCleanupOptions options,
        CancellationToken cancellationToken)
    {
        // AC2 + AC6: with no budget configured (the default) this returns immediately and the
        // sweep is byte-for-byte the behaviour that shipped before. A zero or negative budget
        // takes the same path - disabled, never "a zero-byte budget everything exceeds".
        if (options.ResolveMaxDiskBytes() is null)
            return;

        foreach (var group in rows.GroupBy(row => row.AgentId.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var usages = group.Select(row => new SessionDiskUsage(
                row.SessionId.Value, row.AgentId.Value, row.Status, row.UpdatedAt, row.Bytes)).ToList();
            var plan = SessionDiskBudgetPlanner.BuildPlan(
                usages,
                options,
                sessionId => _turnTracker is not null && _turnTracker.HasLiveTurn(sessionId));

            if (!plan.OverBudget)
                continue;

            if (options.DiskBudgetMode != SessionDiskBudgetMode.Enforce)
            {
                _logger.LogWarning(
                    "Agent {AgentId} session storage is {TotalBytes} bytes, over the {MaxDiskBytes}-byte budget. " +
                    "Disk budget mode is Warn, so nothing was evicted.",
                    group.Key, plan.TotalBytes, plan.MaxDiskBytes);
                continue;
            }

            _logger.LogWarning(
                "Agent {AgentId} session storage is {TotalBytes} bytes, over the {MaxDiskBytes}-byte budget. " +
                "Evicting {EvictionCount} session(s) oldest-first down to {HighWaterBytes} bytes.",
                group.Key, plan.TotalBytes, plan.MaxDiskBytes, plan.Evictions.Count, plan.HighWaterBytes);

            foreach (var eviction in plan.Evictions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var row = group.First(candidate => candidate.SessionId.Value == eviction.SessionId);
                var outcome = await _sessionStore.DeleteIfMatchesAsync(
                    SessionCleanupFence.Capture(row), cancellationToken).ConfigureAwait(false);
                if (outcome == SessionMutationOutcome.Applied && _lifecycleEvents is not null)
                {
                    await _lifecycleEvents.PublishAsync(
                        new SessionLifecycleEvent(
                            eviction.SessionId,
                            eviction.AgentId,
                            SessionLifecycleEventType.Deleted,
                            null),
                        cancellationToken);
                }
            }
        }
    }
}

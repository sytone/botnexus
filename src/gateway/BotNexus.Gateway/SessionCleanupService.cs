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

    /// <summary>
    /// Returns <c>true</c> when the session currently has a live, in-flight agent run and must
    /// therefore be left alone by the sweep. <see cref="GatewaySession.UpdatedAt"/> only advances
    /// on <i>message</i> activity, so an hour-plus agent turn can cross the TTL without touching
    /// it; expiring or deleting such a session would pull it out from under the running turn
    /// (#2395). An in-flight run is treated as an activity signal that refreshes the TTL: the
    /// session simply becomes eligible again on the next sweep after its run completes.
    /// </summary>
    private bool HasInFlightRun(GatewaySession session) =>
        _turnTracker is not null && _turnTracker.HasLiveTurn(session.SessionId.Value);
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
        var options = Options;
        var ttl = options.SessionTtl <= TimeSpan.Zero ? TimeSpan.FromHours(24) : options.SessionTtl;
        var now = DateTimeOffset.UtcNow;
        var sessions = await _sessionStore.ListAsync(cancellationToken: cancellationToken);

        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (HasInFlightRun(session))
            {
                _logger.LogDebug(
                    "Skipping session cleanup for {SessionId}: an agent run is in flight.",
                    session.SessionId.Value);
                continue;
            }

            if (session.Status == SessionStatus.Active && now - session.UpdatedAt > ttl)
            {
                session.Status = SessionStatus.Expired;
                session.ExpiresAt ??= now;
                await _sessionStore.SaveAsync(session, cancellationToken);
                if (_lifecycleEvents is not null)
                {
                    await _lifecycleEvents.PublishAsync(
                        new SessionLifecycleEvent(
                            session.SessionId.Value,
                            session.AgentId.Value,
                            SessionLifecycleEventType.Expired,
                            session),
                        cancellationToken);
                }
                continue;
            }

            if (options.ClosedSessionRetention.HasValue &&
                options.ClosedSessionRetention.Value > TimeSpan.Zero &&
                session.Status == SessionStatus.Sealed &&
                now - session.UpdatedAt > options.ClosedSessionRetention.Value)
            {
                await _sessionStore.DeleteAsync(session.SessionId, cancellationToken);
                if (_lifecycleEvents is not null)
                {
                    await _lifecycleEvents.PublishAsync(
                        new SessionLifecycleEvent(
                            session.SessionId.Value,
                            session.AgentId.Value,
                            SessionLifecycleEventType.Deleted,
                            session),
                        cancellationToken);
                }
                continue;
            }

            if (options.CronNoopRetention.HasValue &&
                options.CronNoopRetention.Value > TimeSpan.Zero &&
                session.SessionId.IsCron &&
                session.MessageCount <= 2 &&
                now - session.UpdatedAt > options.CronNoopRetention.Value)
            {
                await _sessionStore.DeleteAsync(session.SessionId, cancellationToken);
                if (_lifecycleEvents is not null)
                {
                    await _lifecycleEvents.PublishAsync(
                        new SessionLifecycleEvent(
                            session.SessionId.Value,
                            session.AgentId.Value,
                            SessionLifecycleEventType.Deleted,
                            session),
                        cancellationToken);
                }
            }
        }

        await ApplyDiskBudgetAsync(options, cancellationToken);
    }

    /// <summary>
    /// Applies the optional session-directory disk budget (issue #2848) at the end of the same
    /// cleanup cycle, so no second timer is introduced. Sessions are grouped per agent because the
    /// budget is a per-agent directory budget, matching how sessions are laid out on disk.
    /// </summary>
    /// <remarks>
    /// Runs AFTER the age predicates deliberately: whatever TTL/retention already reclaimed is not
    /// counted as pressure, so a correctly-configured age policy keeps the size path dormant.
    /// </remarks>
    private async Task ApplyDiskBudgetAsync(SessionCleanupOptions options, CancellationToken cancellationToken)
    {
        // AC2 + AC6: with no budget configured (the default) this returns immediately and the
        // sweep is byte-for-byte the behaviour that shipped before. A zero or negative budget
        // takes the same path - disabled, never "a zero-byte budget everything exceeds".
        if (options.ResolveMaxDiskBytes() is null)
            return;

        var sessions = await _sessionStore.ListAsync(cancellationToken: cancellationToken);
        foreach (var group in sessions.GroupBy(s => s.AgentId.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var usages = group.Select(SessionDiskAccounting.ToUsage).ToList();
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

                var sessionId = SessionId.From(eviction.SessionId);
                await _sessionStore.DeleteAsync(sessionId, cancellationToken);
                if (_lifecycleEvents is not null)
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

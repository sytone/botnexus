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
    }
}

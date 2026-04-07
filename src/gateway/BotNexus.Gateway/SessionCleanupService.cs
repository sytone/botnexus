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
    SessionLifecycleEvents? lifecycleEvents = null) : BackgroundService
{
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

            if (session.Status == SessionStatus.Active && now - session.UpdatedAt > ttl)
            {
                session.Status = SessionStatus.Expired;
                session.ExpiresAt ??= now;
                await _sessionStore.SaveAsync(session, cancellationToken);
                if (_lifecycleEvents is not null)
                {
                    await _lifecycleEvents.PublishAsync(
                        new SessionLifecycleEvent(
                            session.SessionId,
                            session.AgentId,
                            SessionLifecycleEventType.Expired,
                            session),
                        cancellationToken);
                }
                continue;
            }

            if (options.ClosedSessionRetention.HasValue &&
                options.ClosedSessionRetention.Value > TimeSpan.Zero &&
                session.Status == SessionStatus.Closed &&
                now - session.UpdatedAt > options.ClosedSessionRetention.Value)
            {
                await _sessionStore.DeleteAsync(session.SessionId, cancellationToken);
                if (_lifecycleEvents is not null)
                {
                    await _lifecycleEvents.PublishAsync(
                        new SessionLifecycleEvent(
                            session.SessionId,
                            session.AgentId,
                            SessionLifecycleEventType.Deleted,
                            session),
                        cancellationToken);
                }
            }
        }
    }
}

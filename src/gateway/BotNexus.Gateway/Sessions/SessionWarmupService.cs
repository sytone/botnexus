using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;
using SessionType = BotNexus.Domain.Primitives.SessionType;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Sessions;

public sealed class SessionWarmupService : ISessionWarmupService, IHostedService
{
    private readonly ISessionStore _sessionStore;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IOptions<SessionWarmupOptions> _options;
    private readonly ISessionLifecycleEvents? _lifecycleEvents;
    private readonly ILogger<SessionWarmupService> _logger;
    private readonly ConcurrentDictionary<string, List<SessionSummary>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public SessionWarmupService(
        ISessionStore sessionStore,
        IAgentRegistry agentRegistry,
        IOptions<SessionWarmupOptions> options,
        ILogger<SessionWarmupService> logger,
        ISessionLifecycleEvents? lifecycleEvents = null)
    {
        _sessionStore = sessionStore;
        _agentRegistry = agentRegistry;
        _options = options;
        _logger = logger;
        _lifecycleEvents = lifecycleEvents;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled())
        {
            _logger.LogDebug("Session warmup is disabled.");
            return;
        }

        if (_lifecycleEvents is not null)
        {
            _lifecycleEvents.SessionChanged += OnSessionChangedAsync;
        }

        await RefreshAllInternalAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifecycleEvents is not null)
        {
            _lifecycleEvents.SessionChanged -= OnSessionChangedAsync;
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<SessionSummary>> GetAvailableSessionsAsync(CancellationToken ct = default)
    {
        if (!IsEnabled())
            return [];

        await RefreshAllInternalAsync(ct);
        return _cache.Values
            .SelectMany(static sessions => sessions)
            .OrderByDescending(static session => session.UpdatedAt)
            .ToArray();
    }

    public async Task<IReadOnlyList<SessionSummary>> GetAvailableSessionsAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        if (!IsEnabled())
            return [];

        await RefreshAgentInternalAsync(agentId, ct);

        if (!_cache.TryGetValue(agentId, out var sessions))
            return [];

        return sessions.OrderByDescending(static session => session.UpdatedAt).ToArray();
    }

    private async Task OnSessionChangedAsync(SessionLifecycleEvent lifecycleEvent, CancellationToken cancellationToken)
    {
        if (!IsEnabled())
            return;

        if (string.IsNullOrWhiteSpace(lifecycleEvent.AgentId))
        {
            await RefreshAllInternalAsync(cancellationToken);
            return;
        }

        await RefreshAgentInternalAsync(lifecycleEvent.AgentId, cancellationToken);
    }

    private bool IsEnabled() => _options.Value.Enabled;

    private async Task RefreshAllInternalAsync(CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            var options = _options.Value;
            var agentIds = _agentRegistry.GetAll()
                .Select(static descriptor => descriptor.AgentId.Value)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var existingAgentId in _cache.Keys)
            {
                if (!agentIds.Contains(existingAgentId, StringComparer.OrdinalIgnoreCase))
                    _cache.TryRemove(existingAgentId, out _);
            }

            foreach (var agentId in agentIds)
            {
                var summaries = await BuildSummariesForAgentAsync(agentId, options, ct);
                _cache[agentId] = summaries;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshAgentInternalAsync(string agentId, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            var summaries = await BuildSummariesForAgentAsync(agentId, _options.Value, ct);
            _cache[agentId] = summaries;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<List<SessionSummary>> BuildSummariesForAgentAsync(string agentId, SessionWarmupOptions options, CancellationToken ct)
    {
        var retentionHours = Math.Max(0, options.RetentionWindowHours);
        var updatedAfter = DateTimeOffset.UtcNow.AddHours(-retentionHours);
        var maxSessions = Math.Max(0, options.MaxSessionsPerAgent);

        var sessions = await _sessionStore.ListAsync(AgentId.From(agentId), ct);
        var summaries = GetVisibleSessionsAsync(sessions, updatedAfter, options.CollapseChannelContinuations)
            .OrderByDescending(static session => session.UpdatedAt)
            .Take(maxSessions)
            .Select(static session => new SessionSummary(
                session.SessionId,
                session.AgentId,
                session.ChannelType,
                session.Status,
                session.SessionType,
                session.IsInteractive,
                session.MessageCount,
                session.CreatedAt,
                session.UpdatedAt))
            .ToList();

        _logger.LogDebug("Session warmup cache refreshed for agent {AgentId}: {Count} sessions", agentId, summaries.Count);
        return summaries;
    }

    private static IEnumerable<GatewaySession> GetVisibleSessionsAsync(
        IEnumerable<GatewaySession> sessions,
        DateTimeOffset updatedAfter,
        bool collapseChannelContinuations)
    {
        // Core design: any session that is not Sealed is visible.
        // IsInteractive on the session determines if the user can send messages.
        var visibleCandidates = sessions
            .Where(session =>
                session.Status != GatewaySessionStatus.Sealed
                && session.UpdatedAt >= updatedAfter)
            .ToArray();

        if (!collapseChannelContinuations)
            return visibleCandidates;

        var visible = visibleCandidates
            .Where(static session => !session.ChannelType.HasValue)
            .ToList();

        foreach (var channelSessions in visibleCandidates
                     .Where(static session => session.ChannelType.HasValue)
                     .GroupBy(static session => session.ChannelType!.Value))
        {
            var mostRecentActiveOrSuspended = channelSessions
                .Where(session => session.Status == GatewaySessionStatus.Active || session.Status == GatewaySessionStatus.Suspended)
                .OrderByDescending(static session => session.UpdatedAt)
                .FirstOrDefault();

            if (mostRecentActiveOrSuspended is not null)
            {
                visible.Add(mostRecentActiveOrSuspended);
                continue;
            }

            var mostRecentSealed = channelSessions
                .Where(session => session.Status == GatewaySessionStatus.Sealed)
                .OrderByDescending(static session => session.UpdatedAt)
                .FirstOrDefault();

            if (mostRecentSealed is not null)
                visible.Add(mostRecentSealed);
        }

        return visible;
    }
}

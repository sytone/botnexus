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

            // One transcript-free query for every agent, then bucket in memory. The store
            // never materialises session history here (issue #1581).
            var summaries = await _sessionStore.ListSummariesAsync(ComputeUpdatedAfter(options), ct);
            var byAgent = summaries
                .GroupBy(static summary => summary.AgentId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    static group => group.Key,
                    static group => (IReadOnlyList<SessionSummary>)group.ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var agentId in agentIds)
            {
                var agentSummaries = byAgent.TryGetValue(agentId, out var list)
                    ? list
                    : Array.Empty<SessionSummary>();
                _cache[agentId] = BuildVisibleSummaries(agentSummaries, options);
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
            var options = _options.Value;
            var summaries = await _sessionStore.ListSummariesAsync(ComputeUpdatedAfter(options), ct);
            var agentSummaries = summaries
                .Where(summary => string.Equals(summary.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            _cache[agentId] = BuildVisibleSummaries(agentSummaries, options);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static DateTimeOffset ComputeUpdatedAfter(SessionWarmupOptions options)
        => DateTimeOffset.UtcNow.AddHours(-Math.Max(0, options.RetentionWindowHours));

    private static List<SessionSummary> BuildVisibleSummaries(
        IEnumerable<SessionSummary> summaries,
        SessionWarmupOptions options)
    {
        var maxSessions = Math.Max(0, options.MaxSessionsPerAgent);
        return GetVisibleSummaries(summaries, options.CollapseChannelContinuations)
            .OrderByDescending(static summary => summary.UpdatedAt)
            .Take(maxSessions)
            .ToList();
    }

    private static IEnumerable<SessionSummary> GetVisibleSummaries(
        IEnumerable<SessionSummary> summaries,
        bool collapseChannelContinuations)
    {
        // Only surface user-agent sessions; hide internal types (Soul, Cron, AgentSubAgent, etc.)
        // and sessions delivered via the "cron" channel even if typed as UserAgent. The retention
        // window has already been applied by the store's ListSummariesAsync.
        var visibleCandidates = summaries
            .Where(summary =>
                summary.SessionType.Equals(SessionType.UserAgent)
                && (!summary.ChannelType.HasValue
                    || !string.Equals(summary.ChannelType.Value.Value, "cron", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (!collapseChannelContinuations)
            return visibleCandidates;

        var visible = visibleCandidates
            .Where(static summary => !summary.ChannelType.HasValue)
            .ToList();

        foreach (var channelSessions in visibleCandidates
                     .Where(static summary => summary.ChannelType.HasValue)
                     .GroupBy(static summary => summary.ChannelType!.Value))
        {
            var mostRecentActiveOrSuspended = channelSessions
                .Where(summary => summary.Status == GatewaySessionStatus.Active || summary.Status == GatewaySessionStatus.Suspended)
                .OrderByDescending(static summary => summary.UpdatedAt)
                .FirstOrDefault();

            if (mostRecentActiveOrSuspended is not null)
            {
                visible.Add(mostRecentActiveOrSuspended);
                continue;
            }

            var mostRecentSealed = channelSessions
                .Where(summary => summary.Status == GatewaySessionStatus.Sealed)
                .OrderByDescending(static summary => summary.UpdatedAt)
                .FirstOrDefault();

            if (mostRecentSealed is not null)
                visible.Add(mostRecentSealed);
        }

        return visible;
    }
}

using System.Diagnostics;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// In-memory session store for development and testing.
/// Not durable — all sessions are lost on restart.
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private static readonly ActivitySource ActivitySource = new("BotNexus.Gateway");
    private readonly Dictionary<string, GatewaySession> _sessions = [];
    private readonly Lock _sync = new();

    /// <inheritdoc />
    public Task<GatewaySession?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.get", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);
        lock (_sync) return Task.FromResult(_sessions.GetValueOrDefault(sessionId));
    }

    /// <inheritdoc />
    public Task<GatewaySession> GetOrCreateAsync(string sessionId, string agentId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.get_or_create", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);
        activity?.SetTag("botnexus.agent.id", agentId);
        lock (_sync)
        {
            if (_sessions.TryGetValue(sessionId, out var existing))
                return Task.FromResult(existing);

            var session = new GatewaySession
            {
                SessionId = sessionId,
                AgentId = agentId,
                SessionType = InferSessionType(sessionId, null)
            };
            _sessions[sessionId] = session;
            return Task.FromResult(session);
        }
    }

    /// <inheritdoc />
    public Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.save", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", session.SessionId);
        activity?.SetTag("botnexus.agent.id", session.AgentId);
        lock (_sync) _sessions[session.SessionId] = session;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.delete", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);
        lock (_sync) _sessions.Remove(sessionId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ArchiveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _sessions.Remove(sessionId);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GatewaySession>> ListAsync(string? agentId = null, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            IReadOnlyList<GatewaySession> result = agentId is null
                ? [.. _sessions.Values]
                : _sessions.Values.Where(s => s.AgentId == agentId).ToList();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GatewaySession>> ListByChannelAsync(
        string agentId,
        ChannelKey channelType,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var sessions = _sessions.Values
                .Where(s => s.AgentId == agentId)
                .Where(s => s.ChannelType is not null && s.ChannelType.Value.Equals(channelType))
                .OrderByDescending(s => s.CreatedAt)
                .ToList();
            return Task.FromResult<IReadOnlyList<GatewaySession>>(sessions);
        }
    }

    private static SessionType InferSessionType(string sessionId, ChannelKey? channelType)
    {
        if (sessionId.Contains("::subagent::", StringComparison.OrdinalIgnoreCase))
            return SessionType.AgentSubAgent;

        if (channelType.HasValue && string.Equals(channelType.Value, "cron", StringComparison.OrdinalIgnoreCase))
            return SessionType.Cron;

        return SessionType.UserAgent;
    }
}

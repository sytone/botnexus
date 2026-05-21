using System.Diagnostics;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// In-memory session store for development and testing.
/// Not durable — all sessions are lost on restart.
/// </summary>
public sealed class InMemorySessionStore : SessionStoreBase
{
    private static readonly ActivitySource ActivitySource = new("BotNexus.Gateway");
    private readonly Dictionary<SessionId, GatewaySession> _sessions = [];
    private readonly Lock _sync = new();
    private readonly ISecretRedactor? _redactor;

    /// <summary>Creates an in-memory session store without secret redaction.</summary>
    public InMemorySessionStore() : this(null) { }

    /// <summary>Creates an in-memory session store that redacts secrets at write time.</summary>
    public InMemorySessionStore(ISecretRedactor? redactor)
    {
        _redactor = redactor;
    }

    /// <inheritdoc />
    public override Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.get", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);
        lock (_sync) return Task.FromResult(_sessions.GetValueOrDefault(sessionId));
    }

    /// <inheritdoc />
    public override Task<GatewaySession> GetOrCreateAsync(SessionId sessionId, AgentId agentId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.get_or_create", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);
        activity?.SetTag("botnexus.agent.id", agentId);
        lock (_sync)
        {
            if (_sessions.TryGetValue(sessionId, out var existing))
                return Task.FromResult(existing);

            var session = CreateSession(sessionId, agentId, null, _redactor);
            _sessions[sessionId] = session;
            return Task.FromResult(session);
        }
    }

    /// <inheritdoc />
    public override Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.save", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", session.SessionId);
        activity?.SetTag("botnexus.agent.id", session.AgentId);
        lock (_sync) _sessions[session.SessionId] = session;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.delete", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);
        lock (_sync) _sessions.Remove(sessionId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task ArchiveAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _sessions.Remove(sessionId);
        }
        return Task.CompletedTask;
    }

    protected override Task<IReadOnlyList<GatewaySession>> EnumerateSessionsAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<GatewaySession>>([.. _sessions.Values]);
        }
    }
}

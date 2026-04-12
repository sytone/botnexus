using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Gateway.Sessions;

public abstract class SessionStoreBase : ISessionStore
{
    public abstract Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken cancellationToken = default);

    public abstract Task<GatewaySession> GetOrCreateAsync(SessionId sessionId, AgentId agentId, CancellationToken cancellationToken = default);

    public abstract Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default);

    public abstract Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default);

    public abstract Task ArchiveAsync(SessionId sessionId, CancellationToken cancellationToken = default);

    public async Task<IReadOnlyList<GatewaySession>> ListAsync(AgentId? agentId = null, CancellationToken cancellationToken = default)
    {
        var sessions = await EnumerateSessionsAsync(cancellationToken).ConfigureAwait(false);
        return ApplyAgentFilter(sessions, agentId).ToList();
    }

    public async Task<IReadOnlyList<GatewaySession>> ListAsync(
        AgentId? agentId,
        BotNexus.Gateway.Abstractions.Models.SessionStatus? status,
        CancellationToken cancellationToken = default)
    {
        var sessions = await EnumerateSessionsAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<GatewaySession> result = ApplyAgentFilter(sessions, agentId);
        if (status is not null)
        {
            result = result.Where(session => session.Status == status.Value);
        }

        return result.ToList();
    }

    public async Task<IReadOnlyList<GatewaySession>> ListByChannelAsync(
        AgentId agentId,
        ChannelKey channelType,
        CancellationToken cancellationToken = default)
    {
        var sessions = await EnumerateSessionsAsync(cancellationToken).ConfigureAwait(false);
        return sessions
            .Where(session => session.AgentId == agentId)
            .Where(session => session.ChannelType is not null && session.ChannelType == channelType)
            .OrderByDescending(session => session.CreatedAt)
            .ToList();
    }

    public async Task<IReadOnlyList<GatewaySession>> GetExistenceAsync(
        AgentId agentId,
        ExistenceQuery query,
        CancellationToken cancellationToken = default)
    {
        query ??= new ExistenceQuery();

        var sessions = await EnumerateSessionsAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<GatewaySession> result = sessions
            .Where(session => session.AgentId == agentId || IsParticipant(session, agentId))
            .Where(session => !query.From.HasValue || session.CreatedAt >= query.From.Value)
            .Where(session => !query.To.HasValue || session.CreatedAt <= query.To.Value)
            .Where(session => query.TypeFilter is null || session.SessionType == query.TypeFilter)
            .OrderByDescending(session => session.CreatedAt);

        if (query.Limit is > 0)
            result = result.Take(query.Limit.Value);

        return result.ToList();
    }

    protected static GatewaySession CreateSession(SessionId sessionId, AgentId agentId, ChannelKey? channelType)
        => new()
        {
            SessionId = sessionId,
            AgentId = agentId,
            ChannelType = channelType,
            SessionType = InferSessionType(sessionId, channelType)
        };

    protected static SessionType InferSessionType(SessionId sessionId, ChannelKey? channelType)
    {
        if (sessionId.IsSubAgent)
            return SessionType.AgentSubAgent;

        if (sessionId.IsAgentConversation)
            return SessionType.AgentAgent;

        if (sessionId.IsSoul)
            return SessionType.Soul;

        if (channelType.HasValue && string.Equals(channelType.Value, "cron", StringComparison.OrdinalIgnoreCase))
            return SessionType.Cron;

        return SessionType.UserAgent;
    }

    protected abstract Task<IReadOnlyList<GatewaySession>> EnumerateSessionsAsync(CancellationToken cancellationToken);

    private static IEnumerable<GatewaySession> ApplyAgentFilter(IEnumerable<GatewaySession> sessions, AgentId? agentId)
        => agentId is null ? sessions : sessions.Where(session => session.AgentId == agentId);

    private static bool IsParticipant(GatewaySession session, AgentId agentId)
        => session.Participants.Any(participant =>
            string.Equals(participant.Id, agentId.Value, StringComparison.OrdinalIgnoreCase));
}

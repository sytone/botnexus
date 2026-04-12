using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using ChannelKey = BotNexus.Domain.Primitives.ChannelKey;
using ParticipantType = BotNexus.Domain.Primitives.ParticipantType;
using SessionParticipant = BotNexus.Domain.Primitives.SessionParticipant;
using SessionType = BotNexus.Domain.Primitives.SessionType;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Api.Hubs;

#pragma warning disable CS1591 // Hub methods are self-documenting SignalR contracts

/// <summary>
/// SignalR hub for real-time agent communication. Replaces the raw WebSocket infrastructure.
/// Clients join session groups and receive streaming output for all active sessions simultaneously.
/// </summary>
public sealed class GatewayHub : Hub
{
    private readonly IAgentSupervisor _supervisor;
    private readonly IAgentRegistry _registry;
    private readonly ISessionStore _sessions;
    private readonly IChannelDispatcher _dispatcher;
    private readonly IActivityBroadcaster _activity;
    private readonly ISessionCompactor _compactor;
    private readonly ISessionWarmupService _warmup;
    private readonly IOptions<CompactionOptions> _compactionOptions;
    private readonly ILogger<GatewayHub> _logger;

    public GatewayHub(
        IAgentSupervisor supervisor,
        IAgentRegistry registry,
        ISessionStore sessions,
        IChannelDispatcher dispatcher,
        IActivityBroadcaster activity,
        ISessionCompactor compactor,
        ISessionWarmupService warmup,
        IOptions<CompactionOptions> compactionOptions,
        ILogger<GatewayHub> logger)
    {
        _supervisor = supervisor;
        _registry = registry;
        _sessions = sessions;
        _dispatcher = dispatcher;
        _activity = activity;
        _compactor = compactor;
        _warmup = warmup;
        _compactionOptions = compactionOptions;
        _logger = logger;
    }

    public async Task<object> SubscribeAll()
    {
        var sessions = await _warmup.GetAvailableSessionsAsync(Context.ConnectionAborted);

        foreach (var session in sessions)
        {
            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                GetSessionGroup(session.SessionId),
                Context.ConnectionAborted);
        }

        _logger.LogInformation(
            "Hub SubscribeAll: connection={ConnectionId} sessions={Count}",
            Context.ConnectionId,
            sessions.Count);

        return new { sessions };
    }

    public async Task<object> Subscribe(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            GetSessionGroup(sessionId),
            Context.ConnectionAborted);

        return new
        {
            sessionId,
            status = "subscribed"
        };
    }

    public async Task<object> JoinSession(string agentId, string? sessionId)
    {
        sessionId ??= Guid.NewGuid().ToString("N");
        _logger.LogInformation("Hub JoinSession: agent={AgentId} session={SessionId} connection={ConnectionId} group={Group}",
            agentId, sessionId, Context.ConnectionId, GetSessionGroup(sessionId));
        await Groups.AddToGroupAsync(Context.ConnectionId, GetSessionGroup(sessionId));

        var session = await _sessions.GetOrCreateAsync(sessionId, agentId, Context.ConnectionAborted);

        var needsSave = false;
        if (session.Status == SessionStatus.Expired)
        {
            _logger.LogInformation("Reactivating expired session {SessionId} on join", sessionId);
            session.Status = SessionStatus.Active;
            session.ExpiresAt = null;
            needsSave = true;
        }

        if (session.ChannelType is null)
        {
            session.ChannelType = ChannelKey.From("signalr");
            needsSave = true;
        }

        session.SessionType = SessionType.UserAgent;
        if (session.Participants.Count == 0)
        {
            session.Participants.Add(new SessionParticipant
            {
                Type = ParticipantType.User,
                Id = Context.ConnectionId
            });
            needsSave = true;
        }

        if (needsSave)
        {
            await _sessions.SaveAsync(session, Context.ConnectionAborted);
        }

        return new
        {
            sessionId,
            agentId,
            connectionId = Context.ConnectionId,
            messageCount = session.History.Count,
            isResumed = session.History.Count > 0,
            status = session.Status.ToString(),
            channelType = session.ChannelType,
            createdAt = session.CreatedAt,
            updatedAt = session.UpdatedAt
        };
    }

    public Task LeaveSession(string sessionId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, GetSessionGroup(sessionId));

    public Task SendMessage(string agentId, string sessionId, string content)
    {
        _logger.LogInformation("Hub SendMessage: agent={AgentId} session={SessionId} connection={ConnectionId} content={Content}",
            agentId, sessionId, Context.ConnectionId, content?.Length > 50 ? content[..50] + "..." : content);
        return _dispatcher.DispatchAsync(
            new InboundMessage
            {
                ChannelType = ChannelKey.From("signalr"),
                SenderId = Context.ConnectionId,
                ConversationId = sessionId,
                SessionId = sessionId,
                TargetAgentId = agentId,
                Content = content,
                Metadata = new Dictionary<string, object?> { ["messageType"] = "message" }
            },
            CancellationToken.None);
    }

    public Task Steer(string agentId, string sessionId, string content)
        => _dispatcher.DispatchAsync(
            new InboundMessage
            {
                ChannelType = ChannelKey.From("signalr"),
                SenderId = Context.ConnectionId,
                ConversationId = sessionId,
                SessionId = sessionId,
                TargetAgentId = agentId,
                Content = content,
                Metadata = new Dictionary<string, object?>
                {
                    ["messageType"] = "steer",
                    ["control"] = "steer"
                }
            },
            CancellationToken.None);

    public Task FollowUp(string agentId, string sessionId, string content)
        => SendMessage(agentId, sessionId, content);

    public async Task Abort(string agentId, string sessionId)
    {
        var instance = _supervisor.GetInstance(agentId, sessionId);
        if (instance is null)
            return;

        var handle = await _supervisor.GetOrCreateAsync(agentId, sessionId, CancellationToken.None);
        await handle.AbortAsync(CancellationToken.None);
    }

    public async Task ResetSession(string agentId, string sessionId)
    {
        await _supervisor.StopAsync(agentId, sessionId, CancellationToken.None);
        await _sessions.ArchiveAsync(sessionId, CancellationToken.None);
        await Clients.Caller.SendAsync("SessionReset", new { agentId, sessionId });
    }

    public async Task<object> CompactSession(string agentId, string sessionId)
    {
        var session = await _sessions.GetAsync(sessionId, CancellationToken.None);
        if (session is null)
            throw new HubException($"Session '{sessionId}' not found.");

        var requestServices = Context.GetHttpContext()?.RequestServices;
        var compactor = requestServices?.GetService<ISessionCompactor>() ?? _compactor;
        var options = requestServices?.GetService<IOptions<CompactionOptions>>()?.Value ?? _compactionOptions.Value;

        var result = await compactor.CompactAsync(session, options, CancellationToken.None);
        await _sessions.SaveAsync(session, CancellationToken.None);

        return new
        {
            summarized = result.EntriesSummarized,
            preserved = result.EntriesPreserved,
            tokensBefore = result.TokensBefore,
            tokensAfter = result.TokensAfter
        };
    }

    public Task<IReadOnlyList<AgentDescriptor>> GetAgents()
        => Task.FromResult(_registry.GetAll());

    public AgentInstance? GetAgentStatus(string agentId, string sessionId)
        => _supervisor.GetInstance(agentId, sessionId);

    public override async Task OnConnectedAsync()
    {
        var clientVersion = Context.GetHttpContext()?.Request.Query["clientVersion"].FirstOrDefault() ?? "unknown";
        _logger.LogInformation("Hub OnConnected: connection={ConnectionId} clientVersion={ClientVersion}",
            Context.ConnectionId, clientVersion);

        await Clients.Caller.SendAsync("Connected", new
        {
            connectionId = Context.ConnectionId,
            agents = _registry.GetAll().Select(a => new { a.AgentId, a.DisplayName }),
            serverVersion = typeof(GatewayHub).Assembly.GetName().Version?.ToString() ?? "dev",
            capabilities = new { multiSession = true }
        });

        await _activity.PublishAsync(
            new GatewayActivity
            {
                Type = GatewayActivityType.System,
                ChannelType = ChannelKey.From("signalr"),
                Message = "Web Chat client connected.",
                Data = new Dictionary<string, object?> { ["connectionId"] = Context.ConnectionId }
            },
            Context.ConnectionAborted);

        await base.OnConnectedAsync();
    }

    private static string GetSessionGroup(string sessionId) => $"session:{sessionId}";
}

using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.AspNetCore.SignalR;

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

    public GatewayHub(
        IAgentSupervisor supervisor,
        IAgentRegistry registry,
        ISessionStore sessions,
        IChannelDispatcher dispatcher,
        IActivityBroadcaster activity)
    {
        _supervisor = supervisor;
        _registry = registry;
        _sessions = sessions;
        _dispatcher = dispatcher;
        _activity = activity;
    }

    public async Task JoinSession(string agentId, string? sessionId)
    {
        sessionId ??= Guid.NewGuid().ToString("N");
        await Groups.AddToGroupAsync(Context.ConnectionId, GetSessionGroup(sessionId));

        var session = await _sessions.GetOrCreateAsync(sessionId, agentId, Context.ConnectionAborted);
        await Clients.Caller.SendAsync("SessionJoined", new
        {
            sessionId,
            agentId,
            connectionId = Context.ConnectionId,
            messageCount = session.History.Count
        });
    }

    public Task LeaveSession(string sessionId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, GetSessionGroup(sessionId));

    public Task SendMessage(string agentId, string sessionId, string content)
        => _dispatcher.DispatchAsync(
            new InboundMessage
            {
                ChannelType = "signalr",
                SenderId = Context.ConnectionId,
                ConversationId = sessionId,
                SessionId = sessionId,
                TargetAgentId = agentId,
                Content = content,
                Metadata = new Dictionary<string, object?> { ["messageType"] = "message" }
            },
            CancellationToken.None);

    public Task Steer(string agentId, string sessionId, string content)
        => _dispatcher.DispatchAsync(
            new InboundMessage
            {
                ChannelType = "signalr",
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
        await _sessions.DeleteAsync(sessionId, CancellationToken.None);
        await Clients.Caller.SendAsync("SessionReset", new { agentId, sessionId });
    }

    public Task<IReadOnlyList<AgentDescriptor>> GetAgents()
        => Task.FromResult(_registry.GetAll());

    public AgentInstance? GetAgentStatus(string agentId, string sessionId)
        => _supervisor.GetInstance(agentId, sessionId);

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("Connected", new
        {
            connectionId = Context.ConnectionId,
            agents = _registry.GetAll().Select(a => new { a.AgentId, a.DisplayName })
        });

        await _activity.PublishAsync(
            new GatewayActivity
            {
                Type = GatewayActivityType.System,
                ChannelType = "signalr",
                Message = "SignalR client connected.",
                Data = new Dictionary<string, object?> { ["connectionId"] = Context.ConnectionId }
            },
            Context.ConnectionAborted);

        await base.OnConnectedAsync();
    }

    private static string GetSessionGroup(string sessionId) => $"session:{sessionId}";
}

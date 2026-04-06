using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using BotNexus.Channels.Core.Diagnostics;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Api.WebSocket;
using NetWebSocket = System.Net.WebSockets.WebSocket;
using NetWebSocketMessageType = System.Net.WebSockets.WebSocketMessageType;
using NetWebSocketState = System.Net.WebSockets.WebSocketState;

/// <summary>
/// Dispatches inbound WebSocket messages to the appropriate gateway action and persists outbound stream state.
/// </summary>
public sealed class WebSocketMessageDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IAgentSupervisor _supervisor;
    private readonly IGatewayWebSocketChannelAdapter _channelAdapter;
    private readonly ISessionStore _sessions;
    private readonly IOptions<GatewayWebSocketOptions> _webSocketOptions;
    private readonly WebSocketConnectionManager _connectionManager;
    private readonly ILogger<WebSocketMessageDispatcher> _logger;

    /// <summary>
    /// Initializes a new dispatcher.
    /// </summary>
    public WebSocketMessageDispatcher(
        IAgentSupervisor supervisor,
        IGatewayWebSocketChannelAdapter channelAdapter,
        ISessionStore sessions,
        IOptions<GatewayWebSocketOptions> webSocketOptions,
        WebSocketConnectionManager connectionManager,
        ILogger<WebSocketMessageDispatcher> logger)
    {
        _supervisor = supervisor;
        _channelAdapter = channelAdapter;
        _sessions = sessions;
        _webSocketOptions = webSocketOptions;
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <summary>
    /// Sends the initial connected event to a newly accepted socket.
    /// </summary>
    public Task SendConnectedAsync(
        GatewaySession session,
        string sessionId,
        NetWebSocket socket,
        string connectionId,
        int replayWindow,
        CancellationToken cancellationToken)
        => SendSequencedJsonAsync(
            session,
            sessionId,
            socket,
            new { type = "connected", connectionId, sessionId },
            replayWindow,
            cancellationToken);

    /// <summary>
    /// Continuously processes inbound client messages while the socket remains open.
    /// </summary>
    public async Task ProcessMessagesAsync(
        NetWebSocket socket,
        string connectionId,
        string agentId,
        string sessionId,
        GatewaySession session,
        int replayWindow,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        while (socket.State == NetWebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == NetWebSocketMessageType.Close)
                break;

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var message = JsonSerializer.Deserialize<WsClientMessage>(json, JsonOptions);
            if (message is null)
                continue;

            if (await _connectionManager.TryHandlePingAsync(
                    message,
                    (payload, ct) => SendSequencedJsonAsync(session, sessionId, socket, payload, replayWindow, ct),
                    cancellationToken))
            {
                continue;
            }

            switch (message.Type)
            {
                case "message" when message.Content is not null:
                    await HandleUserMessageAsync(socket, connectionId, agentId, sessionId, message.Content, message.Type, cancellationToken);
                    break;

                case "abort":
                    await HandleAbortAsync(agentId, sessionId, cancellationToken);
                    break;

                case "steer" when message.Content is not null:
                    await HandleSteerAsync(socket, agentId, sessionId, message.Content, cancellationToken);
                    break;

                case "follow_up" when message.Content is not null:
                    await HandleFollowUpAsync(socket, agentId, sessionId, message.Content, cancellationToken);
                    break;

                case "reconnect":
                    await HandleReconnectAsync(
                        socket,
                        agentId,
                        message.SessionKey ?? sessionId,
                        message.LastSeqId ?? 0,
                        replayWindow,
                        cancellationToken);
                    break;
            }
        }
    }

    /// <summary>
    /// Allocates sequence IDs, stores replay events, and persists session state for outbound payloads.
    /// </summary>
    public async ValueTask<object> SequenceAndPersistPayloadAsync(
        GatewaySession session,
        object payload,
        int replayWindow,
        CancellationToken cancellationToken)
    {
        var sequenceId = session.ReplayBuffer.AllocateSequenceId();
        var basePayloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var payloadMap = JsonSerializer.Deserialize<Dictionary<string, object?>>(basePayloadJson, JsonOptions) ?? [];
        payloadMap["sequenceId"] = sequenceId;
        object sequencedPayload = payloadMap;
        var sequencedPayloadJson = JsonSerializer.Serialize(sequencedPayload, JsonOptions);
        session.ReplayBuffer.AddStreamEvent(sequenceId, sequencedPayloadJson, replayWindow);
        session.UpdatedAt = DateTimeOffset.UtcNow;

        using var saveActivity = GatewayDiagnostics.Source.StartActivity("session.save", ActivityKind.Internal);
        saveActivity?.SetTag("botnexus.session.id", session.SessionId);
        saveActivity?.SetTag("botnexus.agent.id", session.AgentId);
        await _sessions.SaveAsync(session, cancellationToken);

        return sequencedPayload;
    }

    private async Task HandleAbortAsync(string agentId, string sessionId, CancellationToken cancellationToken)
    {
        var handle = _supervisor.GetInstance(agentId, sessionId);
        if (handle is null)
            return;

        var agentHandle = await _supervisor.GetOrCreateAsync(agentId, sessionId, cancellationToken);
        await agentHandle.AbortAsync(cancellationToken);
    }

    private async Task HandleUserMessageAsync(
        NetWebSocket socket,
        string connectionId,
        string agentId,
        string sessionId,
        string content,
        string messageType,
        CancellationToken cancellationToken)
    {
        try
        {
            await _channelAdapter.DispatchInboundMessageAsync(
                agentId,
                sessionId,
                connectionId,
                content,
                messageType,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling WebSocket message for agent '{AgentId}'", agentId);
            await SendSessionErrorAsync(socket, agentId, sessionId, ex.Message, "AGENT_ERROR", cancellationToken);
        }
    }

    private async Task HandleSteerAsync(
        NetWebSocket socket,
        string agentId,
        string sessionId,
        string content,
        CancellationToken cancellationToken)
    {
        using var activity = ChannelDiagnostics.Source.StartActivity("channel.steer", ActivityKind.Internal);
        activity?.SetTag("botnexus.channel.type", "websocket");
        activity?.SetTag("botnexus.message.type", "steer");
        activity?.SetTag("botnexus.session.id", sessionId);
        activity?.SetTag("botnexus.agent.id", agentId);

        var instance = _supervisor.GetInstance(agentId, sessionId);
        if (instance is null)
        {
            await SendSessionErrorAsync(socket, agentId, sessionId, "Agent session not found.", "SESSION_NOT_FOUND", cancellationToken);
            return;
        }

        var handle = await _supervisor.GetOrCreateAsync(agentId, sessionId, cancellationToken);
        await handle.SteerAsync(content, cancellationToken);
    }

    private async Task HandleFollowUpAsync(
        NetWebSocket socket,
        string agentId,
        string sessionId,
        string content,
        CancellationToken cancellationToken)
    {
        var instance = _supervisor.GetInstance(agentId, sessionId);
        if (instance is null)
        {
            await SendSessionErrorAsync(socket, agentId, sessionId, "Agent session not found.", "SESSION_NOT_FOUND", cancellationToken);
            return;
        }

        var handle = await _supervisor.GetOrCreateAsync(agentId, sessionId, cancellationToken);
        await handle.FollowUpAsync(content, cancellationToken);
    }

    private async Task HandleReconnectAsync(
        NetWebSocket socket,
        string agentId,
        string sessionKey,
        long lastSeqId,
        int replayWindow,
        CancellationToken cancellationToken)
    {
        using var getActivity = GatewayDiagnostics.Source.StartActivity("session.get", ActivityKind.Internal);
        getActivity?.SetTag("botnexus.session.id", sessionKey);

        var session = await _sessions.GetAsync(sessionKey, cancellationToken);
        if (session is null || !string.Equals(session.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
        {
            await SendSessionErrorAsync(socket, agentId, sessionKey, "Session not found for reconnect.", "SESSION_NOT_FOUND", cancellationToken);
            return;
        }

        var replayEvents = session.ReplayBuffer.GetStreamEventsAfter(lastSeqId, replayWindow);
        foreach (var replayEvent in replayEvents)
        {
            if (socket.State != NetWebSocketState.Open)
                break;

            var payload = Encoding.UTF8.GetBytes(replayEvent.PayloadJson);
            await socket.SendAsync(payload, NetWebSocketMessageType.Text, true, cancellationToken);
        }

        await SendSequencedJsonAsync(
            session,
            sessionKey,
            socket,
            new
            {
                type = "reconnect_ack",
                sessionKey,
                replayed = replayEvents.Count,
                lastSeqId
            },
            replayWindow,
            cancellationToken);
    }

    private async Task SendSessionErrorAsync(
        NetWebSocket socket,
        string agentId,
        string sessionId,
        string message,
        string code,
        CancellationToken cancellationToken)
    {
        using var sessionActivity = GatewayDiagnostics.Source.StartActivity("session.get_or_create", ActivityKind.Internal);
        sessionActivity?.SetTag("botnexus.session.id", sessionId);
        sessionActivity?.SetTag("botnexus.agent.id", agentId);

        var session = await _sessions.GetOrCreateAsync(sessionId, agentId, cancellationToken);
        await SendSequencedJsonAsync(
            session,
            sessionId,
            socket,
            new { type = "error", message, code },
            Math.Max(_webSocketOptions.Value.ReplayWindowSize, 1),
            cancellationToken);
    }

    private async Task SendSequencedJsonAsync(
        GatewaySession session,
        string sessionId,
        NetWebSocket socket,
        object message,
        int replayWindow,
        CancellationToken cancellationToken)
    {
        var sequenced = await SequenceAndPersistPayloadAsync(session, message, replayWindow, cancellationToken);
        var json = JsonSerializer.SerializeToUtf8Bytes(sequenced, JsonOptions);
        await socket.SendAsync(json, NetWebSocketMessageType.Text, true, cancellationToken);
    }
}

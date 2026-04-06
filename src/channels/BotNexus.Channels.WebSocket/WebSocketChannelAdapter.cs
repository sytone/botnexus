using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Channels.Core;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;
using NetWebSocket = System.Net.WebSockets.WebSocket;

namespace BotNexus.Channels.WebSocket;

/// <summary>
/// WebSocket channel adapter that integrates gateway WebSocket sessions with the channel pipeline.
/// </summary>
public sealed class WebSocketChannelAdapter(ILogger<WebSocketChannelAdapter> logger)
    : ChannelAdapterBase(logger), IStreamEventChannelAdapter
{
    private readonly ConcurrentDictionary<string, ConnectionRegistration> _connections = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public override string ChannelType => "websocket";

    public override string DisplayName => "Gateway WebSocket";

    public override bool SupportsStreaming => true;

    public override bool SupportsSteering => true;

    public override bool SupportsFollowUp => true;

    public override bool SupportsThinkingDisplay => true;

    public override bool SupportsToolDisplay => true;

    protected override Task OnStartAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    protected override Task OnStopAsync(CancellationToken cancellationToken)
    {
        _connections.Clear();
        return Task.CompletedTask;
    }

    public bool RegisterConnection(string sessionId, string connectionId, NetWebSocket socket)
        => _connections.TryAdd(sessionId, new ConnectionRegistration(connectionId, socket));

    public void UnregisterConnection(string sessionId, string connectionId)
    {
        if (_connections.TryGetValue(sessionId, out var registration) &&
            string.Equals(registration.ConnectionId, connectionId, StringComparison.Ordinal))
        {
            _connections.TryRemove(sessionId, out _);
        }
    }

    public async Task DispatchInboundMessageAsync(
        string agentId,
        string sessionId,
        string senderId,
        string content,
        CancellationToken cancellationToken)
    {
        await DispatchInboundAsync(new InboundMessage
        {
            ChannelType = ChannelType,
            SenderId = senderId,
            ConversationId = sessionId,
            SessionId = sessionId,
            TargetAgentId = agentId,
            Content = content
        }, cancellationToken);
    }

    public override Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
        => SendPayloadAsync(
            message.ConversationId,
            new
            {
                type = "content_delta",
                delta = message.Content,
                sessionId = message.SessionId
            },
            cancellationToken);

    public override Task SendStreamDeltaAsync(string conversationId, string delta, CancellationToken cancellationToken = default)
        => SendPayloadAsync(conversationId, new { type = "content_delta", delta }, cancellationToken);

    public Task SendStreamEventAsync(string conversationId, AgentStreamEvent streamEvent, CancellationToken cancellationToken = default)
    {
        object payload = streamEvent.Type switch
        {
            AgentStreamEventType.MessageStart => new { type = "message_start", messageId = streamEvent.MessageId },
            AgentStreamEventType.ThinkingDelta => new { type = "thinking_delta", delta = streamEvent.ThinkingContent, messageId = streamEvent.MessageId },
            AgentStreamEventType.ContentDelta => new { type = "content_delta", delta = streamEvent.ContentDelta, messageId = streamEvent.MessageId },
            AgentStreamEventType.ToolStart => new { type = "tool_start", toolCallId = streamEvent.ToolCallId, toolName = streamEvent.ToolName, messageId = streamEvent.MessageId },
            AgentStreamEventType.ToolEnd => new { type = "tool_end", toolCallId = streamEvent.ToolCallId, toolResult = streamEvent.ToolResult, messageId = streamEvent.MessageId },
            AgentStreamEventType.MessageEnd => new { type = "message_end", messageId = streamEvent.MessageId, usage = streamEvent.Usage },
            AgentStreamEventType.Error => new { type = "error", message = streamEvent.ErrorMessage },
            _ => new { type = "unknown" }
        };

        return SendPayloadAsync(conversationId, payload, cancellationToken);
    }

    private async Task SendPayloadAsync(string conversationId, object payload, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(conversationId, out var registration))
        {
            Logger.LogDebug("No active WebSocket connection for session '{SessionId}'", conversationId);
            return;
        }

        var socket = registration.Socket;
        if (socket is null || socket.State != WebSocketState.Open)
            return;

        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await socket.SendAsync(json, WebSocketMessageType.Text, true, cancellationToken);
    }

    private sealed record ConnectionRegistration(string ConnectionId, NetWebSocket? Socket);
}

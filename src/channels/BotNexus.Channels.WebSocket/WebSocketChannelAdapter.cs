using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Channels.Core.Diagnostics;
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
    : ChannelAdapterBase(logger), IGatewayWebSocketChannelAdapter
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

    public bool RegisterConnection(
        string sessionId,
        string connectionId,
        NetWebSocket socket,
        Func<object, CancellationToken, ValueTask<object>>? payloadMutator = null)
        => _connections.TryAdd(sessionId, new ConnectionRegistration(connectionId, socket, payloadMutator));

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
        string messageType = "message",
        CancellationToken cancellationToken = default)
    {
        using var activity = ChannelDiagnostics.Source.StartActivity("channel.receive", ActivityKind.Server);
        activity?.SetTag("botnexus.channel.type", ChannelType);
        activity?.SetTag("botnexus.message.type", messageType);
        activity?.SetTag("botnexus.session.id", sessionId);

        using var steerActivity = string.Equals(messageType, "steer", StringComparison.OrdinalIgnoreCase)
            ? ChannelDiagnostics.Source.StartActivity("channel.steer", ActivityKind.Internal)
            : null;
        steerActivity?.SetTag("botnexus.channel.type", ChannelType);
        steerActivity?.SetTag("botnexus.message.type", messageType);
        steerActivity?.SetTag("botnexus.session.id", sessionId);

        await DispatchInboundAsync(new InboundMessage
        {
            ChannelType = ChannelType,
            SenderId = senderId,
            ConversationId = sessionId,
            SessionId = sessionId,
            TargetAgentId = agentId,
            Content = content,
            Metadata = new Dictionary<string, object?>
            {
                ["messageType"] = messageType
            }
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
            "content_delta",
            cancellationToken);

    public override Task SendStreamDeltaAsync(string conversationId, string delta, CancellationToken cancellationToken = default)
        => SendPayloadAsync(conversationId, new { type = "content_delta", delta }, "content_delta", cancellationToken);

    public Task SendStreamEventAsync(string conversationId, AgentStreamEvent streamEvent, CancellationToken cancellationToken = default)
    {
        var messageType = streamEvent.Type switch
        {
            AgentStreamEventType.MessageStart => "message_start",
            AgentStreamEventType.ThinkingDelta => "thinking_delta",
            AgentStreamEventType.ContentDelta => "content_delta",
            AgentStreamEventType.ToolStart => "tool_start",
            AgentStreamEventType.ToolEnd => "tool_end",
            AgentStreamEventType.MessageEnd => "message_end",
            AgentStreamEventType.Error => "error",
            _ => "unknown"
        };

        object payload = streamEvent.Type switch
        {
            AgentStreamEventType.MessageStart => new { type = "message_start", messageId = streamEvent.MessageId },
            AgentStreamEventType.ThinkingDelta => new { type = "thinking_delta", delta = streamEvent.ThinkingContent, messageId = streamEvent.MessageId },
            AgentStreamEventType.ContentDelta => new { type = "content_delta", delta = streamEvent.ContentDelta, messageId = streamEvent.MessageId },
            AgentStreamEventType.ToolStart => new { type = "tool_start", toolCallId = streamEvent.ToolCallId, toolName = streamEvent.ToolName, messageId = streamEvent.MessageId },
            AgentStreamEventType.ToolEnd => new { type = "tool_end", toolCallId = streamEvent.ToolCallId, toolName = streamEvent.ToolName, toolResult = streamEvent.ToolResult, toolIsError = streamEvent.ToolIsError, messageId = streamEvent.MessageId },
            AgentStreamEventType.MessageEnd => new { type = "message_end", messageId = streamEvent.MessageId, usage = streamEvent.Usage },
            AgentStreamEventType.Error => new { type = "error", message = streamEvent.ErrorMessage },
            _ => new { type = "unknown" }
        };

        return SendPayloadAsync(conversationId, payload, messageType, cancellationToken);
    }

    private async Task SendPayloadAsync(string conversationId, object payload, string messageType, CancellationToken cancellationToken)
    {
        using var activity = ChannelDiagnostics.Source.StartActivity("channel.send", ActivityKind.Client);
        activity?.SetTag("botnexus.channel.type", ChannelType);
        activity?.SetTag("botnexus.message.type", messageType);
        activity?.SetTag("botnexus.session.id", conversationId);

        if (!_connections.TryGetValue(conversationId, out var registration))
        {
            Logger.LogDebug("No active WebSocket connection for session '{SessionId}'", conversationId);
            return;
        }

        var socket = registration.Socket;
        if (socket is null || socket.State != WebSocketState.Open)
            return;

        if (registration.PayloadMutator is not null)
        {
            payload = await registration.PayloadMutator(payload, cancellationToken);
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await socket.SendAsync(json, WebSocketMessageType.Text, true, cancellationToken);
    }

    private sealed record ConnectionRegistration(
        string ConnectionId,
        NetWebSocket? Socket,
        Func<object, CancellationToken, ValueTask<object>>? PayloadMutator);
}

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Streaming;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Api.WebSocket;

/// <summary>
/// Handles WebSocket connections for real-time agent interaction.
/// </summary>
/// <remarks>
/// <para>WebSocket Protocol:</para>
/// <para><b>Connection:</b> <c>ws://host/ws?agent={agentId}&amp;session={sessionId}</c></para>
/// <para><b>Client → Server messages:</b></para>
/// <list type="bullet">
///   <item><c>{ "type": "message", "content": "..." }</c> — Send a message to the agent.</item>
///   <item><c>{ "type": "abort" }</c> — Abort the current agent execution.</item>
///   <item><c>{ "type": "ping" }</c> — Keepalive ping.</item>
/// </list>
/// <para><b>Server → Client messages:</b></para>
/// <list type="bullet">
///   <item><c>{ "type": "connected", "connectionId": "...", "sessionId": "..." }</c></item>
///   <item><c>{ "type": "message_start", "messageId": "..." }</c></item>
///   <item><c>{ "type": "thinking_delta", "delta": "...", "messageId": "..." }</c></item>
///   <item><c>{ "type": "content_delta", "delta": "...", "messageId": "..." }</c></item>
///   <item><c>{ "type": "tool_start", "toolCallId": "...", "toolName": "...", "messageId": "..." }</c></item>
///   <item><c>{ "type": "tool_end", "toolCallId": "...", "toolResult": "...", "messageId": "..." }</c></item>
///   <item><c>{ "type": "message_end", "messageId": "...", "usage": { ... } }</c></item>
///   <item><c>{ "type": "error", "message": "...", "code": "..." }</c></item>
///   <item><c>{ "type": "pong" }</c></item>
/// </list>
/// </remarks>
public sealed class GatewayWebSocketHandler
{
    private readonly IAgentSupervisor _supervisor;
    private readonly ISessionStore _sessions;
    private readonly IActivityBroadcaster _activity;
    private readonly ILogger<GatewayWebSocketHandler> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GatewayWebSocketHandler(
        IAgentSupervisor supervisor,
        ISessionStore sessions,
        IActivityBroadcaster activity,
        ILogger<GatewayWebSocketHandler> logger)
    {
        _supervisor = supervisor;
        _sessions = sessions;
        _activity = activity;
        _logger = logger;
    }

    /// <summary>
    /// Handles an incoming WebSocket connection.
    /// </summary>
    public async Task HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        var agentId = context.Request.Query["agent"].FirstOrDefault();
        var sessionId = context.Request.Query["session"].FirstOrDefault() ?? Guid.NewGuid().ToString("N");

        if (string.IsNullOrEmpty(agentId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Missing 'agent' query parameter", cancellationToken);
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connectionId = Guid.NewGuid().ToString("N");

        _logger.LogInformation("WebSocket connected: {ConnectionId} agent={AgentId} session={SessionId}", connectionId, agentId, sessionId);

        // Send connected message
        await SendJsonAsync(socket, new { type = "connected", connectionId, sessionId }, cancellationToken);

        try
        {
            await ProcessMessagesAsync(socket, agentId, sessionId, cancellationToken);
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogDebug("WebSocket closed prematurely: {ConnectionId}", connectionId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("WebSocket cancelled: {ConnectionId}", connectionId);
        }
        finally
        {
            _logger.LogInformation("WebSocket disconnected: {ConnectionId}", connectionId);
        }
    }

    private async Task ProcessMessagesAsync(System.Net.WebSockets.WebSocket socket, string agentId, string sessionId, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var msg = JsonSerializer.Deserialize<WsClientMessage>(json, JsonOptions);
            if (msg is null) continue;

            switch (msg.Type)
            {
                case "message" when msg.Content is not null:
                    await HandleUserMessageAsync(socket, agentId, sessionId, msg.Content, cancellationToken);
                    break;

                case "abort":
                    var handle = _supervisor.GetInstance(agentId, sessionId);
                    if (handle is not null)
                    {
                        var agentHandle = await _supervisor.GetOrCreateAsync(agentId, sessionId, cancellationToken);
                        await agentHandle.AbortAsync(cancellationToken);
                    }
                    break;

                case "ping":
                    await SendJsonAsync(socket, new { type = "pong" }, cancellationToken);
                    break;
            }
        }
    }

    private async Task HandleUserMessageAsync(System.Net.WebSockets.WebSocket socket, string agentId, string sessionId, string content, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetOrCreateAsync(sessionId, agentId, cancellationToken);
        session.AddEntry(new SessionEntry { Role = "user", Content = content });
        var sessionSaved = false;

        try
        {
            var handle = await _supervisor.GetOrCreateAsync(agentId, sessionId, cancellationToken);

            await StreamingSessionHelper.ProcessAndSaveAsync(
                handle.StreamAsync(content, cancellationToken),
                session,
                _sessions,
                new StreamingSessionOptions(
                    OnEventAsync: (evt, ct) =>
                    {
                        object wsMessage = evt.Type switch
                        {
                            AgentStreamEventType.MessageStart => new { type = "message_start", messageId = evt.MessageId },
                            AgentStreamEventType.ThinkingDelta => new { type = "thinking_delta", delta = evt.ThinkingContent, messageId = evt.MessageId },
                            AgentStreamEventType.ContentDelta => new { type = "content_delta", delta = evt.ContentDelta, messageId = evt.MessageId },
                            AgentStreamEventType.ToolStart => new { type = "tool_start", toolCallId = evt.ToolCallId, toolName = evt.ToolName, messageId = evt.MessageId },
                            AgentStreamEventType.ToolEnd => new { type = "tool_end", toolCallId = evt.ToolCallId, toolResult = evt.ToolResult, messageId = evt.MessageId },
                            AgentStreamEventType.MessageEnd => new { type = "message_end", messageId = evt.MessageId, usage = evt.Usage },
                            AgentStreamEventType.Error => new { type = "error", message = evt.ErrorMessage },
                            _ => (object)new { type = "unknown" }
                        };

                        return new ValueTask(SendJsonAsync(socket, wsMessage, ct));
                    }),
                cancellationToken);
            sessionSaved = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling WebSocket message for agent '{AgentId}'", agentId);
            await SendJsonAsync(socket, new { type = "error", message = ex.Message, code = "AGENT_ERROR" }, cancellationToken);
        }

        if (!sessionSaved)
        {
            await _sessions.SaveAsync(session, cancellationToken);
        }
    }

    private static async Task SendJsonAsync(System.Net.WebSockets.WebSocket socket, object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await socket.SendAsync(json, WebSocketMessageType.Text, true, cancellationToken);
    }

    private sealed record WsClientMessage(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("content")] string? Content = null);
}

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Globalization;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Streaming;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
///   <item><c>{ "type": "steer", "content": "..." }</c> — Inject steering message into active run.</item>
///   <item><c>{ "type": "follow_up", "content": "..." }</c> — Queue follow-up for next run.</item>
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
    private readonly IOptions<GatewayWebSocketOptions> _webSocketOptions;
    private readonly ILogger<GatewayWebSocketHandler> _logger;
    private readonly ConcurrentDictionary<string, ConnectionAttemptWindow> _connectionAttempts = new(StringComparer.OrdinalIgnoreCase);
    private long _connectionAttemptUpdates;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GatewayWebSocketHandler(
        IAgentSupervisor supervisor,
        ISessionStore sessions,
        IActivityBroadcaster activity,
        IOptions<GatewayWebSocketOptions> webSocketOptions,
        ILogger<GatewayWebSocketHandler> logger)
    {
        _supervisor = supervisor;
        _sessions = sessions;
        _activity = activity;
        _webSocketOptions = webSocketOptions;
        _logger = logger;
    }

    public GatewayWebSocketHandler(
        IAgentSupervisor supervisor,
        ISessionStore sessions,
        IActivityBroadcaster activity,
        ILogger<GatewayWebSocketHandler> logger)
        : this(supervisor, sessions, activity, Options.Create(new GatewayWebSocketOptions()), logger)
    {
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

        if (!TryRegisterConnectionAttempt(context, agentId, out var retryAfter))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            var retrySeconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
            if (retrySeconds > 0)
                context.Response.Headers["Retry-After"] = retrySeconds.ToString(CultureInfo.InvariantCulture);

            await context.Response.WriteAsync(
                $"Reconnect limit exceeded. Retry in {Math.Max(retrySeconds, 1)} second(s).",
                cancellationToken);
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

                case "steer" when msg.Content is not null:
                    await HandleSteerAsync(socket, agentId, sessionId, msg.Content, cancellationToken);
                    break;

                case "follow_up" when msg.Content is not null:
                    await HandleFollowUpAsync(socket, agentId, sessionId, msg.Content, cancellationToken);
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

    private async Task HandleSteerAsync(System.Net.WebSockets.WebSocket socket, string agentId, string sessionId, string content, CancellationToken cancellationToken)
    {
        var instance = _supervisor.GetInstance(agentId, sessionId);
        if (instance is null)
        {
            await SendJsonAsync(socket, new { type = "error", message = "Agent session not found.", code = "SESSION_NOT_FOUND" }, cancellationToken);
            return;
        }

        var handle = await _supervisor.GetOrCreateAsync(agentId, sessionId, cancellationToken);
        await handle.SteerAsync(content, cancellationToken);
    }

    private async Task HandleFollowUpAsync(System.Net.WebSockets.WebSocket socket, string agentId, string sessionId, string content, CancellationToken cancellationToken)
    {
        var instance = _supervisor.GetInstance(agentId, sessionId);
        if (instance is null)
        {
            await SendJsonAsync(socket, new { type = "error", message = "Agent session not found.", code = "SESSION_NOT_FOUND" }, cancellationToken);
            return;
        }

        var handle = await _supervisor.GetOrCreateAsync(agentId, sessionId, cancellationToken);
        await handle.FollowUpAsync(content, cancellationToken);
    }

    private static async Task SendJsonAsync(System.Net.WebSockets.WebSocket socket, object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await socket.SendAsync(json, WebSocketMessageType.Text, true, cancellationToken);
    }

    private bool TryRegisterConnectionAttempt(HttpContext context, string agentId, out TimeSpan retryAfter)
    {
        var options = _webSocketOptions.Value;
        var maxAttempts = Math.Max(options.MaxReconnectAttempts, 1);
        var attemptWindow = TimeSpan.FromSeconds(Math.Max(options.AttemptWindowSeconds, 1));
        var backoffBase = TimeSpan.FromSeconds(Math.Max(options.BackoffBaseSeconds, 1));
        var backoffMax = TimeSpan.FromSeconds(Math.Max(options.BackoffMaxSeconds, options.BackoffBaseSeconds));
        var now = DateTimeOffset.UtcNow;
        var clientKey = GetClientAttemptKey(context, agentId);

        while (true)
        {
            if (!_connectionAttempts.TryGetValue(clientKey, out var current))
            {
                if (_connectionAttempts.TryAdd(clientKey, new ConnectionAttemptWindow(now, 1)))
                {
                    retryAfter = TimeSpan.Zero;
                    CleanupStaleAttemptWindows(attemptWindow, now);
                    return true;
                }

                continue;
            }

            if (now - current.WindowStartedUtc >= attemptWindow)
            {
                if (_connectionAttempts.TryUpdate(clientKey, new ConnectionAttemptWindow(now, 1), current))
                {
                    retryAfter = TimeSpan.Zero;
                    CleanupStaleAttemptWindows(attemptWindow, now);
                    return true;
                }

                continue;
            }

            if (current.AttemptCount >= maxAttempts)
            {
                var penaltyAttempt = current.AttemptCount - maxAttempts + 1;
                var retrySeconds = Math.Min(
                    backoffBase.TotalSeconds * Math.Pow(2, penaltyAttempt - 1),
                    backoffMax.TotalSeconds);
                retryAfter = TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling(retrySeconds)));
                return false;
            }

            var updated = current with { AttemptCount = current.AttemptCount + 1 };
            if (_connectionAttempts.TryUpdate(clientKey, updated, current))
            {
                retryAfter = TimeSpan.Zero;
                CleanupStaleAttemptWindows(attemptWindow, now);
                return true;
            }
        }
    }

    private static string GetClientAttemptKey(HttpContext context, string agentId)
    {
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        var clientAddress = context.Connection.RemoteIpAddress?.ToString();
        var clientId = string.IsNullOrWhiteSpace(forwardedFor)
            ? clientAddress
            : forwardedFor.Split(',')[0].Trim();

        return $"{(string.IsNullOrWhiteSpace(clientId) ? "unknown" : clientId)}::{agentId}";
    }

    private void CleanupStaleAttemptWindows(TimeSpan attemptWindow, DateTimeOffset now)
    {
        if (Interlocked.Increment(ref _connectionAttemptUpdates) % 128 != 0)
            return;

        foreach (var (key, value) in _connectionAttempts)
        {
            if (now - value.WindowStartedUtc >= attemptWindow + attemptWindow)
                _connectionAttempts.TryRemove(key, out _);
        }
    }

    private sealed record WsClientMessage(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("content")] string? Content = null);

    private readonly record struct ConnectionAttemptWindow(DateTimeOffset WindowStartedUtc, int AttemptCount);
}

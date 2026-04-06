using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Sessions;
using System.Diagnostics;
using BotNexus.Gateway.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Api.WebSocket;
using NetWebSocket = System.Net.WebSockets.WebSocket;
using NetWebSocketCloseStatus = System.Net.WebSockets.WebSocketCloseStatus;
using NetWebSocketError = System.Net.WebSockets.WebSocketError;
using NetWebSocketException = System.Net.WebSockets.WebSocketException;

/// <summary>
/// Handles WebSocket connections for real-time agent interaction.
/// </summary>
/// <remarks>
/// <para>WebSocket Protocol:</para>
/// <para><b>Connection:</b> <c>ws://host/ws?agent={agentId}&amp;session={sessionId}</c></para>
/// <para><b>Client → Server messages:</b></para>
/// <list type="bullet">
///   <item><c>{ "type": "message", "content": "..." }</c> — Send a message to the agent.</item>
///   <item><c>{ "type": "reconnect", "sessionKey": "...", "lastSeqId": 42 }</c> — Replay missed outbound events.</item>
///   <item><c>{ "type": "abort" }</c> — Abort the current agent execution.</item>
///   <item><c>{ "type": "steer", "content": "..." }</c> — Inject steering message into active run.</item>
///   <item><c>{ "type": "follow_up", "content": "..." }</c> — Queue follow-up for next run.</item>
///   <item><c>{ "type": "ping" }</c> — Keepalive ping.</item>
/// </list>
/// <para><b>Server → Client messages:</b></para>
/// <list type="bullet">
///   <item><c>{ "type": "connected", "connectionId": "...", "sessionId": "...", "sequenceId": 1 }</c></item>
///   <item><c>{ "type": "message_start", "messageId": "..." }</c></item>
///   <item><c>{ "type": "thinking_delta", "delta": "...", "messageId": "..." }</c></item>
///   <item><c>{ "type": "content_delta", "delta": "...", "messageId": "..." }</c></item>
///   <item><c>{ "type": "tool_start", "toolCallId": "...", "toolName": "...", "messageId": "..." }</c></item>
///   <item><c>{ "type": "tool_end", "toolCallId": "...", "toolName": "...", "toolResult": "...", "toolIsError": false, "messageId": "..." }</c></item>
///   <item><c>{ "type": "message_end", "messageId": "...", "usage": { ... } }</c></item>
///   <item><c>{ "type": "error", "message": "...", "code": "..." }</c></item>
///   <item><c>{ "type": "pong" }</c></item>
/// </list>
/// </remarks>
public sealed class GatewayWebSocketHandler
{
    private readonly IGatewayWebSocketChannelAdapter _channelAdapter;
    private readonly ISessionStore _sessions;
    private readonly IOptions<GatewayWebSocketOptions> _webSocketOptions;
    private readonly WebSocketConnectionManager _connectionManager;
    private readonly WebSocketMessageDispatcher _dispatcher;
    private readonly ILogger<GatewayWebSocketHandler> _logger;

    /// <summary>
    /// Initializes a new handler that orchestrates connection lifecycle and message dispatch.
    /// </summary>
    public GatewayWebSocketHandler(
        IGatewayWebSocketChannelAdapter channelAdapter,
        ISessionStore sessions,
        IOptions<GatewayWebSocketOptions> webSocketOptions,
        WebSocketConnectionManager connectionManager,
        WebSocketMessageDispatcher dispatcher,
        ILogger<GatewayWebSocketHandler> logger)
    {
        _channelAdapter = channelAdapter;
        _sessions = sessions;
        _webSocketOptions = webSocketOptions;
        _connectionManager = connectionManager;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>
    /// Handles an incoming WebSocket connection.
    /// </summary>
    public async Task HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var agentId = context.Request.Query["agent"].FirstOrDefault();
        var sessionId = context.Request.Query["session"].FirstOrDefault() ?? Guid.NewGuid().ToString("N");

        if (string.IsNullOrEmpty(agentId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Missing 'agent' query parameter", cancellationToken);
            return;
        }

        if (!_connectionManager.TryRegisterConnectionAttempt(context, agentId, out var retryAfter))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            var retrySeconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
            if (retrySeconds > 0)
                context.Response.Headers["Retry-After"] = retrySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

            await context.Response.WriteAsync(
                $"Reconnect limit exceeded. Retry in {Math.Max(retrySeconds, 1)} second(s).",
                cancellationToken);
            return;
        }

        var connectionId = Guid.NewGuid().ToString("N");
        if (!_connectionManager.TryReserveSession(sessionId, connectionId))
        {
            await _connectionManager.CloseDuplicateSessionAsync(context, cancellationToken);
            return;
        }

        NetWebSocket? socket = null;
        try
        {
            socket = await context.WebSockets.AcceptWebSocketAsync();
            using var sessionActivity = GatewayDiagnostics.Source.StartActivity("session.get_or_create", ActivityKind.Internal);
            sessionActivity?.SetTag("botnexus.session.id", sessionId);
            sessionActivity?.SetTag("botnexus.agent.id", agentId);
            var session = await _sessions.GetOrCreateAsync(sessionId, agentId, cancellationToken);
            var replayWindow = Math.Max(_webSocketOptions.Value.ReplayWindowSize, 1);

            _logger.LogInformation("WebSocket connected: {ConnectionId} agent={AgentId} session={SessionId}", connectionId, agentId, sessionId);
            if (!_channelAdapter.RegisterConnection(
                    sessionId,
                    connectionId,
                    socket,
                    (payload, ct) => _dispatcher.SequenceAndPersistPayloadAsync(session, payload, replayWindow, ct)))
            {
                await socket.CloseAsync(
                    (NetWebSocketCloseStatus)WebSocketConnectionManager.SessionAlreadyConnectedCloseCode,
                    "Session already has an active connection",
                    cancellationToken);
                return;
            }

            await _dispatcher.SendConnectedAsync(session, sessionId, socket, connectionId, replayWindow, cancellationToken);
            await _dispatcher.ProcessMessagesAsync(socket, connectionId, agentId, sessionId, session, replayWindow, cancellationToken);
        }
        catch (NetWebSocketException ex) when (ex.WebSocketErrorCode == NetWebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogDebug("WebSocket closed prematurely: {ConnectionId}", connectionId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("WebSocket cancelled: {ConnectionId}", connectionId);
        }
        finally
        {
            _channelAdapter.UnregisterConnection(sessionId, connectionId);
            socket?.Dispose();
            _connectionManager.ReleaseSession(sessionId, connectionId);
            _logger.LogInformation("WebSocket disconnected: {ConnectionId}", connectionId);
        }
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Api.WebSocket;

/// <summary>
/// Manages WebSocket connection admission, session locks, reconnect throttling, and keepalive pong responses.
/// </summary>
public sealed class WebSocketConnectionManager
{
    /// <summary>
    /// Custom close code returned when a session already has an active WebSocket connection.
    /// </summary>
    public const int SessionAlreadyConnectedCloseCode = 4409;

    private readonly IOptions<GatewayWebSocketOptions> _webSocketOptions;
    private readonly ILogger<WebSocketConnectionManager> _logger;
    private readonly ConcurrentDictionary<string, ConnectionAttemptWindow> _connectionAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _activeSessionConnections = new(StringComparer.OrdinalIgnoreCase);
    private long _connectionAttemptUpdates;

    /// <summary>
    /// Initializes a new connection manager.
    /// </summary>
    public WebSocketConnectionManager(
        IOptions<GatewayWebSocketOptions> webSocketOptions,
        ILogger<WebSocketConnectionManager> logger)
    {
        _webSocketOptions = webSocketOptions;
        _logger = logger;
    }

    /// <summary>
    /// Registers a reconnect attempt and enforces throttling windows.
    /// </summary>
    public bool TryRegisterConnectionAttempt(HttpContext context, string agentId, out TimeSpan retryAfter)
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

    /// <summary>
    /// Reserves a session slot for an active WebSocket connection.
    /// </summary>
    public bool TryReserveSession(string sessionId, string connectionId)
        => _activeSessionConnections.TryAdd(sessionId, connectionId);

    /// <summary>
    /// Closes a duplicate session connection with the gateway's session-conflict code.
    /// </summary>
    public async Task CloseDuplicateSessionAsync(HttpContext context, CancellationToken cancellationToken)
    {
        using var duplicateSocket = await context.WebSockets.AcceptWebSocketAsync();
        await duplicateSocket.CloseAsync(
            (System.Net.WebSockets.WebSocketCloseStatus)SessionAlreadyConnectedCloseCode,
            "Session already has an active connection",
            cancellationToken);
    }

    /// <summary>
    /// Releases a reserved session slot and resets reconnect throttling for the client.
    /// </summary>
    public void ReleaseSession(string sessionId, string connectionId)
    {
        _activeSessionConnections.TryRemove(new KeyValuePair<string, string>(sessionId, connectionId));

        // Reset reconnect throttle for all keys matching this session's client
        // so clean disconnects don't penalize subsequent reconnects
        foreach (var key in _connectionAttempts.Keys)
        {
            _connectionAttempts.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Handles keepalive ping messages by sending a pong payload through the sequenced sender.
    /// </summary>
    internal Task<bool> TryHandlePingAsync(
        WsClientMessage message,
        Func<object, CancellationToken, Task> sendAsync,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(message.Type, "ping", StringComparison.Ordinal))
            return Task.FromResult(false);

        _logger.LogTrace("WebSocket ping received; responding with pong.");
        return SendPongAsync(sendAsync, cancellationToken);
    }

    private static async Task<bool> SendPongAsync(
        Func<object, CancellationToken, Task> sendAsync,
        CancellationToken cancellationToken)
    {
        await sendAsync(new { type = "pong" }, cancellationToken);
        return true;
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

    private readonly record struct ConnectionAttemptWindow(DateTimeOffset WindowStartedUtc, int AttemptCount);
}

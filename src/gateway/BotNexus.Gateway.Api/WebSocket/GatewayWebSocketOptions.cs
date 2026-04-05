namespace BotNexus.Gateway.Api.WebSocket;

/// <summary>
/// Runtime limits for inbound WebSocket reconnection attempts.
/// </summary>
public sealed class GatewayWebSocketOptions
{
    /// <summary>
    /// Maximum reconnection attempts per client/agent within the tracking window.
    /// </summary>
    public int MaxReconnectAttempts { get; set; } = 20;

    /// <summary>
    /// Sliding window duration used to count reconnect attempts.
    /// </summary>
    public int AttemptWindowSeconds { get; set; } = 300;

    /// <summary>
    /// Base retry delay (seconds) returned after hitting reconnect limits.
    /// </summary>
    public int BackoffBaseSeconds { get; set; } = 1;

    /// <summary>
    /// Maximum retry delay (seconds) returned after hitting reconnect limits.
    /// </summary>
    public int BackoffMaxSeconds { get; set; } = 60;
}

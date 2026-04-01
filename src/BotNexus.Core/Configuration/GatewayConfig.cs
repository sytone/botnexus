namespace BotNexus.Core.Configuration;

/// <summary>Heartbeat configuration.</summary>
public class HeartbeatConfig
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 1800;
}

/// <summary>Gateway server configuration.</summary>
public class GatewayConfig
{
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 18790;
    public HeartbeatConfig Heartbeat { get; set; } = new();

    /// <summary>Whether the WebSocket endpoint is enabled.</summary>
    public bool WebSocketEnabled { get; set; } = true;

    /// <summary>Path at which the WebSocket endpoint is mounted.</summary>
    public string WebSocketPath { get; set; } = "/ws";
}

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// A real-time activity event broadcast by the Gateway for monitoring and UI updates.
/// Subscribers (WebSocket clients, dashboards) receive these as they happen.
/// </summary>
public sealed record GatewayActivity
{
    /// <summary>Unique event identifier.</summary>
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>The type of activity.</summary>
    public required GatewayActivityType Type { get; init; }

    /// <summary>The agent involved, if any.</summary>
    public string? AgentId { get; init; }

    /// <summary>The session involved, if any.</summary>
    public string? SessionId { get; init; }

    /// <summary>The channel involved, if any.</summary>
    public string? ChannelType { get; init; }

    /// <summary>Human-readable summary of the activity.</summary>
    public string? Message { get; init; }

    /// <summary>When the activity occurred.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Extensible payload data.</summary>
    public IReadOnlyDictionary<string, object?>? Data { get; init; }
}

/// <summary>
/// Categories of gateway activity events.
/// </summary>
public enum GatewayActivityType
{
    /// <summary>A message was received from a channel.</summary>
    MessageReceived,

    /// <summary>A response was sent to a channel.</summary>
    ResponseSent,

    /// <summary>A streaming delta was sent to a channel.</summary>
    StreamDelta,

    /// <summary>An agent started processing a request.</summary>
    AgentProcessing,

    /// <summary>An agent completed processing.</summary>
    AgentCompleted,

    /// <summary>A tool execution started.</summary>
    ToolExecutionStarted,

    /// <summary>A tool execution completed.</summary>
    ToolExecutionCompleted,

    /// <summary>An agent instance was created.</summary>
    AgentStarted,

    /// <summary>An agent instance was stopped.</summary>
    AgentStopped,

    /// <summary>An agent was registered in the registry.</summary>
    AgentRegistered,

    /// <summary>An agent was removed from the registry.</summary>
    AgentUnregistered,

    /// <summary>An agent descriptor was updated in the registry.</summary>
    AgentConfigChanged,

    /// <summary>A session was created.</summary>
    SessionCreated,

    /// <summary>A sub-agent background run was spawned.</summary>
    SubAgentSpawned,

    /// <summary>A sub-agent run completed successfully.</summary>
    SubAgentCompleted,

    /// <summary>A sub-agent run failed or timed out.</summary>
    SubAgentFailed,

    /// <summary>A sub-agent run was explicitly killed.</summary>
    SubAgentKilled,

    /// <summary>An error occurred.</summary>
    Error,

    /// <summary>A system-level informational event.</summary>
    System
}

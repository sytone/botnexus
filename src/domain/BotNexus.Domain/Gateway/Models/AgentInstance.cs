using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Represents a live agent instance bound to a specific session.
/// Tracks runtime status and provides the handle for interaction.
/// </summary>
public sealed class AgentInstance
{
    /// <summary>Unique identifier for this instance (agent + session composite).</summary>
    public required string InstanceId { get; init; }

    /// <summary>The registered agent this instance was created from.</summary>
    public required AgentId AgentId { get; init; }

    /// <summary>The session this instance is bound to.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Current execution status.</summary>
    public AgentInstanceStatus Status { get; set; } = AgentInstanceStatus.Starting;

    /// <summary>When this instance was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the instance last processed a message.</summary>
    public DateTimeOffset? LastActiveAt { get; set; }

    /// <summary>The isolation strategy this instance runs under.</summary>
    public required string IsolationStrategy { get; init; }
}

/// <summary>
/// Runtime status of an agent instance.
/// </summary>
public enum AgentInstanceStatus
{
    /// <summary>Instance is being created and initialized.</summary>
    Starting,

    /// <summary>Instance is ready to accept messages.</summary>
    Idle,

    /// <summary>Instance is actively processing a request.</summary>
    Running,

    /// <summary>Instance is being shut down.</summary>
    Stopping,

    /// <summary>Instance has been stopped and is no longer usable.</summary>
    Stopped,

    /// <summary>Instance encountered a fatal error.</summary>
    Faulted
}

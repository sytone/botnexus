using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Represents runtime information for a spawned sub-agent session.
/// </summary>
public sealed record SubAgentInfo
{
    /// <summary>
    /// Gets the unique sub-agent identifier.
    /// </summary>
    public required string SubAgentId { get; init; }

    /// <summary>
    /// Gets the parent session identifier that owns this sub-agent.
    /// </summary>
    public required SessionId ParentSessionId { get; init; }

    /// <summary>
    /// Gets the child session identifier used by the sub-agent.
    /// </summary>
    public required SessionId ChildSessionId { get; init; }

    /// <summary>
    /// Gets an optional friendly name for the sub-agent.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the delegated task assigned to the sub-agent.
    /// </summary>
    public required string Task { get; init; }

    /// <summary>
    /// Gets the model selected for the sub-agent run.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets the behavioral archetype used for this sub-agent run.
    /// </summary>
    public SubAgentArchetype Archetype { get; init; } = SubAgentArchetype.General;

    /// <summary>
    /// Gets the current execution status.
    /// </summary>
    public SubAgentStatus Status { get; init; } = SubAgentStatus.Running;

    /// <summary>
    /// Gets when the sub-agent started.
    /// </summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// Gets when the sub-agent completed, if finished.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Gets the number of turns consumed by the sub-agent.
    /// </summary>
    public int TurnsUsed { get; init; }

    /// <summary>
    /// Gets an optional completion summary produced by the sub-agent.
    /// </summary>
    public string? ResultSummary { get; init; }
}

/// <summary>
/// Represents the lifecycle state of a sub-agent run.
/// </summary>
public enum SubAgentStatus
{
    /// <summary>
    /// The sub-agent is currently running.
    /// </summary>
    Running,

    /// <summary>
    /// The sub-agent completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// The sub-agent failed.
    /// </summary>
    Failed,

    /// <summary>
    /// The sub-agent was explicitly terminated.
    /// </summary>
    Killed,

    /// <summary>
    /// The sub-agent timed out before completion.
    /// </summary>
    TimedOut
}

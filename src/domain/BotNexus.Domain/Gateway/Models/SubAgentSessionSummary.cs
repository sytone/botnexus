namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// A read-only summary of a persisted sub-agent session row, returned by the
/// <c>GET /api/sessions/{sessionId}/sub-agents/history</c> endpoint.
/// </summary>
/// <remarks>
/// Unlike <see cref="SubAgentInfo"/> (which reflects live runtime state), this
/// record is sourced from the <c>sub_agent_sessions</c> SQLite table and
/// represents historical, post-completion data including end time and final status.
/// </remarks>
public sealed record SubAgentSessionSummary
{
    /// <summary>
    /// Gets the unique sub-agent run identifier (matches <see cref="SubAgentInfo.SubAgentId"/>).
    /// </summary>
    public required string SubAgentId { get; init; }

    /// <summary>
    /// Gets the parent session that spawned this sub-agent.
    /// </summary>
    public required string ParentSessionId { get; init; }

    /// <summary>
    /// Gets the agent ID that was used as the parent.
    /// </summary>
    public required string ParentAgentId { get; init; }

    /// <summary>
    /// Gets the agent ID of the spawned sub-agent.
    /// </summary>
    public required string ChildAgentId { get; init; }

    /// <summary>
    /// Gets the behavioral archetype used for this run, or <c>null</c> if not set.
    /// </summary>
    public string? Archetype { get; init; }

    /// <summary>
    /// Gets the UTC time the sub-agent was spawned.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// Gets the UTC time the sub-agent completed, or <c>null</c> if still active.
    /// </summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>
    /// Gets the final status of the sub-agent run (e.g., Active, Completed, Failed).
    /// </summary>
    public required string Status { get; init; }
}

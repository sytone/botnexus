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
    /// Gets the parent agent identifier. Populated at spawn time for session persistence.
    /// </summary>
    public string? ParentAgentId { get; init; }

    /// <summary>
    /// Gets the child agent identifier. Populated at spawn time for session persistence.
    /// </summary>
    public string? ChildAgentId { get; init; }

    /// <summary>
    /// Gets the conversation this sub-agent run owns. Distinct from the parent's conversation
    /// (issue #2338): a sub-agent run is a full conversation, so it gets its own id and is linked
    /// back to its supervisor by <c>Conversation.ParentConversationId</c> rather than by sharing
    /// the parent's identity. <c>null</c> only when no <c>IConversationStore</c> is wired (tests,
    /// minimal hosts), in which case there is nothing to mint against.
    /// </summary>
    public ConversationId? ChildConversationId { get; init; }

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
    TimedOut,

    /// <summary>
    /// The sub-agent exhausted its turn budget (<c>maxTurns</c>) before producing a final
    /// response. Distinct from <see cref="TimedOut"/>: the run ran out of turns, not wall clock,
    /// so the remedy is a larger turn budget or a narrower task rather than a longer deadline
    /// (#2656).
    /// </summary>
    BudgetExhausted,

    /// <summary>
    /// The run accepted a sub-agent spawn, produced zero delivery payloads, and emitted no
    /// synthesized text of its own - it delegated and correctly stayed silent (#2725).
    /// <para>
    /// <b>Why this is its own state rather than <see cref="Completed"/> or <see cref="Failed"/>.</b>
    /// Delivery classification used to key solely on the run's own final text, so "the run
    /// produced nothing" and "the run delegated and correctly stayed silent" collapsed onto the
    /// same empty-response diagnostic. Under a cron trigger that turned every correctly-delegating
    /// scheduled job red and dropped the descendant's output on the floor. Reusing
    /// <see cref="Completed"/> would fix the red run but erase the distinction an operator needs
    /// ("did this job answer, or did it hand off?"), and it would make suppression of the
    /// empty-response diagnostic indistinguishable from a genuine empty run.
    /// </para>
    /// <para>
    /// A handoff is a SUCCESS state - <c>SubAgentStatusPolicy.IsUnsuccessfulTermination</c>
    /// returns <c>false</c> - but a distinguishable one. The summary it carries is the
    /// DESCENDANT's result, not a diagnostic. A run whose descendant itself failed is recorded as
    /// <see cref="Failed"/>, never as a handoff: the classification must not launder a failed
    /// delegation into a success.
    /// </para>
    /// </summary>
    HandedOff
}

using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Describes a request to spawn a background sub-agent session.
/// </summary>
public sealed record SubAgentSpawnRequest
{
    /// <summary>
    /// Gets the parent agent identifier initiating the spawn.
    /// </summary>
    public required AgentId ParentAgentId { get; init; }

    /// <summary>
    /// Gets the parent session identifier that owns the sub-agent.
    /// </summary>
    public required SessionId ParentSessionId { get; init; }

    /// <summary>
    /// Gets the delegated task prompt for the sub-agent.
    /// </summary>
    public required string Task { get; init; }

    /// <summary>
    /// Gets the maximum number of turns the sub-agent may execute.
    /// </summary>
    public int MaxTurns { get; init; } = 30;

    /// <summary>
    /// Gets the timeout, in seconds, for the sub-agent execution.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 600;

    /// <summary>
    /// Gets the spawn depth of this request within the sub-agent tree.
    /// Zero means the parent is a top-level session; one means the parent is itself a sub-agent.
    /// Used to enforce <see cref="BotNexus.Gateway.Configuration.SubAgentOptions.MaxDepth"/>.
    /// </summary>
    public int SpawnDepth { get; init; }

    /// <summary>
    /// Gets the union of tool names that the parent agent is denied, inherited from the
    /// parent's effective deny-list. The spawned sub-agent must not be granted any of these tools.
    /// </summary>
    public IReadOnlyList<string>? ParentToolDenyList { get; init; }

    /// <summary>
    /// The <em>parent's</em> conversation - the supervising thread this sub-agent run is nested
    /// under. Since #2338 this is the <b>parent edge</b>, not the child's identity: the child gets
    /// its own minted <see cref="ConversationId"/> and records this value as
    /// <c>Conversation.ParentConversationId</c>. Previously it was assigned directly onto the child
    /// session's <c>ConversationId</c>, which collapsed two conversations onto one id and broadcast
    /// every child tool call and delta into the parent's SignalR group.
    /// Enforced by Vogen so the wrapper is non-empty; enforced by <c>required</c> so callers cannot
    /// forget to supply it (a parentless sub-agent run would be unreachable - see
    /// <c>SubAgentEagerPinArchitectureTests</c>).
    /// </summary>
    public required ConversationId InheritedConversationId { get; init; }

    /// <summary>
    /// The id of the parent-side tool call (typically <c>spawn_subagent</c>) that requested this
    /// run, when the caller knows it. Recorded on the child conversation as
    /// <c>Conversation.SpawningToolCallId</c> so a channel can render the run as an expandable card
    /// in place of that exact call rather than guessing the association by timestamp (#2338).
    /// <c>null</c> for spawns that do not originate from a tool call.
    /// </summary>
    public string? SpawningToolCallId { get; init; }

    /// <summary>
    /// The spawn mode: <see cref="Embody"/> a role with optional customisations, or
    /// <see cref="Mirror"/> a registered named agent. Introduced in Phase 5 / F-6
    /// (#562) to replace the bag of optional top-level fields (TargetAgentId /
    /// SystemPromptOverride / etc.) with an explicit discriminated union.
    /// Required: every spawn must pick a mode at construction time.
    /// </summary>
    public required SubAgentSpawnMode Mode { get; init; }

    /// <summary>
    /// When <c>true</c>, the sub-agent is granted read and write access to the
    /// parent agent's workspace directory in addition to its own temporary workspace.
    /// Default is <c>false</c> (fully isolated).
    /// </summary>
    public bool ShareWorkspace { get; init; }

    /// <summary>
    /// Optional list of absolute paths the sub-agent is granted read access to,
    /// beyond its own workspace. Each entry is resolved and validated at spawn time.
    /// </summary>
    public IReadOnlyList<string>? GrantedPaths { get; init; }
}

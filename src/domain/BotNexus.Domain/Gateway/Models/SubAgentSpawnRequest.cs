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
    /// The parent's conversation that this sub-agent's child session must be bound to.
    /// Required because sub-agent output must always remain visible in the parent's
    /// conversation thread — orphan sub-agent sessions are not a supported state.
    /// Enforced by Vogen so the wrapper is non-empty; enforced by <c>required</c> so
    /// callers cannot forget to supply it.
    /// </summary>
    public required ConversationId InheritedConversationId { get; init; }

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

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
    /// Gets an optional friendly name for the sub-agent instance.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets an optional model override for the sub-agent run.
    /// </summary>
    public string? ModelOverride { get; init; }

    /// <summary>
    /// Gets an optional API provider override for the sub-agent run.
    /// </summary>
    public string? ApiProviderOverride { get; init; }

    /// <summary>
    /// Gets an optional tool allowlist for the sub-agent.
    /// </summary>
    public IReadOnlyList<string>? ToolIds { get; init; }

    /// <summary>
    /// Gets an optional system prompt override for the sub-agent.
    /// </summary>
    public string? SystemPromptOverride { get; init; }

    /// <summary>
    /// Gets the maximum number of turns the sub-agent may execute.
    /// </summary>
    public int MaxTurns { get; init; } = 30;

    /// <summary>
    /// Gets the timeout, in seconds, for the sub-agent execution.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 600;

    /// <summary>
    /// Gets the behavioral archetype to apply to the sub-agent.
    /// </summary>
    public SubAgentArchetype Archetype { get; init; } = SubAgentArchetype.General;

    /// <summary>
    /// Gets the spawn depth of this request within the sub-agent tree.
    /// Zero means the parent is a top-level session; one means the parent is itself a sub-agent.
    /// Used to enforce <see cref="BotNexus.Gateway.Configuration.SubAgentOptions.MaxDepth"/>.
    /// </summary>
    public int SpawnDepth { get; init; }

    /// <summary>
    /// Optional registered agent ID to use as the sub-agent identity. When set, the sub-agent
    /// runs as this agent's descriptor (system prompt, model, tools) rather than as a clone of the parent.
    /// </summary>
    public string? TargetAgentId { get; init; }

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
    /// step 3 (#562) to replace the bag of optional top-level fields
    /// (<see cref="TargetAgentId"/> / <see cref="SystemPromptOverride"/> / etc.)
    /// with an explicit discriminated union.
    ///
    /// <para>OPTIONAL during the migration window. When set, supersedes the
    /// equivalent top-level fields. Will become <c>required</c> once all
    /// callers are migrated (step 5 of the migration).</para>
    /// </summary>
    public SubAgentSpawnMode? Mode { get; init; }
}

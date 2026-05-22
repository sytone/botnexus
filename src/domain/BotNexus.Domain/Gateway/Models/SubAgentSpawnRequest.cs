using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;

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
    /// Gets the optional conversation ID inherited from the parent session.
    /// When set, the sub-agent will route its task into this existing conversation
    /// instead of creating a new one — keeping all output visible in the same portal thread.
    /// </summary>
    public string? InheritedConversationId { get; init; }

    /// <summary>
    /// Gets additional file paths the sub-agent is allowed to read.
    /// These are merged with the parent's <see cref="FileAccessPolicy.AllowedReadPaths" /> at spawn time.
    /// Only paths the parent itself can read are accepted; others are silently filtered.
    /// </summary>
    public IReadOnlyList<string> AdditionalReadPaths { get; init; } = [];

    /// <summary>
    /// Gets additional file paths the sub-agent is allowed to write.
    /// These are merged with the parent's <see cref="FileAccessPolicy.AllowedWritePaths" /> at spawn time.
    /// Only paths the parent itself can write are accepted; others are silently filtered.
    /// </summary>
    public IReadOnlyList<string> AdditionalWritePaths { get; init; } = [];
}

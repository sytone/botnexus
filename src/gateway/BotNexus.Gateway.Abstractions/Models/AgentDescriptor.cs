namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Describes a registered agent — its identity, default configuration, and capabilities.
/// This is the static descriptor; runtime state lives in <see cref="AgentInstance"/>.
/// </summary>
public sealed record AgentDescriptor
{
    /// <summary>Unique agent identifier (e.g., "coding-agent", "chat-assistant").</summary>
    public required string AgentId { get; init; }

    /// <summary>Human-readable display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Optional description of the agent's purpose.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// The LLM model identifier this agent uses by default (e.g., "claude-sonnet-4-20250514").
    /// Can be overridden per-session.
    /// </summary>
    public required string ModelId { get; init; }

    /// <summary>
    /// The API provider key (e.g., "anthropic", "openai", "copilot").
    /// Resolved through <c>ApiProviderRegistry</c> at runtime.
    /// </summary>
    public required string ApiProvider { get; init; }

    /// <summary>System prompt template for the agent.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Tool identifiers this agent has access to.
    /// Resolved through the tool registry at agent creation time.
    /// </summary>
    public IReadOnlyList<string> ToolIds { get; init; } = [];

    /// <summary>
    /// The isolation strategy to use when running this agent.
    /// Defaults to <c>"in-process"</c> if not specified.
    /// </summary>
    public string IsolationStrategy { get; init; } = "in-process";

    /// <summary>
    /// Maximum concurrent sessions allowed for this agent.
    /// Zero means unlimited.
    /// </summary>
    public int MaxConcurrentSessions { get; init; }

    /// <summary>
    /// Agent-level metadata for extensibility (tags, owner, etc.).
    /// </summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
        new Dictionary<string, object?>();
}

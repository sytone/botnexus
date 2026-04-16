using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Describes a registered agent — its identity, default configuration, and capabilities.
/// This is the static descriptor; runtime state lives in <see cref="AgentInstance"/>.
/// </summary>
public sealed record AgentDescriptor
{
    /// <summary>Unique agent identifier (e.g., "coding-agent", "chat-assistant").</summary>
    public required AgentId AgentId { get; init; }

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
    /// Path to a file containing the system prompt (alternative to <see cref="SystemPrompt" />).
    /// Relative paths are resolved from the agent configuration directory.
    /// </summary>
    public string? SystemPromptFile { get; init; }

    /// <summary>
    /// Ordered list of system prompt file paths to load and concatenate.
    /// Resolved relative to the agent's workspace directory.
    /// If empty, uses default load order: AGENTS.md, SOUL.md, TOOLS.md, BOOTSTRAP.md, IDENTITY.md, USER.md.
    /// </summary>
    public IReadOnlyList<string> SystemPromptFiles { get; init; } = [];

    /// <summary>
    /// Tool identifiers this agent has access to.
    /// Resolved through the tool registry at agent creation time.
    /// </summary>
    public IReadOnlyList<string> ToolIds { get; init; } = [];

    /// <summary>
    /// Model IDs this agent is allowed to use. Empty means unrestricted within provider allowlist.
    /// </summary>
    public IReadOnlyList<string> AllowedModelIds { get; init; } = [];

    /// <summary>
    /// Agent IDs this agent can call as sub-agents.
    /// </summary>
    public IReadOnlyList<string> SubAgentIds { get; init; } = [];

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

    /// <summary>
    /// Strategy-specific isolation configuration for <see cref="IsolationStrategy" />.
    /// </summary>
    public IReadOnlyDictionary<string, object?> IsolationOptions { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>Memory system configuration for this agent. Null means memory is disabled.</summary>
    public MemoryAgentConfig? Memory { get; init; }

    /// <summary>Soul session lifecycle configuration for this agent. Null means soul sessions are disabled.</summary>
    public SoulAgentConfig? Soul { get; init; }

    /// <summary>Heartbeat polling configuration for this agent. Null means heartbeat is disabled.</summary>
    public HeartbeatAgentConfig? Heartbeat { get; init; }

    /// <summary>Session access level for this agent's session tool. Defaults to "own".</summary>
    public string SessionAccessLevel { get; init; } = "own";

    /// <summary>File system access policy for this agent. Null means workspace-only access.</summary>
    public FileAccessPolicy? FileAccess { get; init; }

    /// <summary>Agent IDs this agent can view sessions for (when SessionAccessLevel is "allowlist").</summary>
    public IReadOnlyList<string> SessionAllowedAgents { get; init; } = [];

    /// <summary>
    /// Extension-specific configuration keyed by extension ID.
    /// Extensions read their own config from this bag using their ID as the key.
    /// Example: <c>ExtensionConfig["botnexus-skills"]</c> for skills config.
    /// </summary>
    public IReadOnlyDictionary<string, System.Text.Json.JsonElement> ExtensionConfig { get; init; } =
        new Dictionary<string, System.Text.Json.JsonElement>();
}

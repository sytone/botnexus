using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Describes a registered agent — its identity, default configuration, and capabilities.
/// This is the static descriptor; runtime state lives in <see cref="AgentInstance"/>.
/// </summary>
public sealed record AgentDescriptor : ICitizen
{
    /// <summary>Unique agent identifier (e.g., "coding-agent", "chat-assistant").</summary>
    public required AgentId AgentId { get; init; }

    /// <summary>Human-readable display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Discriminates a named (configuration / REST-registered) agent from a runtime-spawned
    /// sub-agent. Defaults to <see cref="AgentKind.Named"/> so existing JSON payloads round-trip
    /// unchanged. <see cref="AgentKind.SubAgent"/> is set exclusively by
    /// <c>DefaultSubAgentManager.SpawnAsync</c>; configuration sources that attempt to declare
    /// <see cref="AgentKind.SubAgent"/> are rejected by
    /// <c>AgentDescriptorValidator.ValidateForConfig</c>.
    /// </summary>
    /// <remarks>
    /// Used by <c>InProcessIsolationStrategy</c> as the primary signal for blocking recursive
    /// spawn-tool registration. The SessionId-substring fallback (<c>SessionId.IsSubAgent</c>)
    /// is retained as defense-in-depth for sessions created before this property existed or by
    /// future paths that bypass <c>DefaultSubAgentManager</c>.
    /// </remarks>
    public AgentKind Kind { get; init; } = AgentKind.Named;

    /// <summary>
    /// Discriminated citizen identity. Always <see cref="CitizenId.Of(AgentId)"/> for an agent —
    /// satisfies <see cref="ICitizen"/> so the descriptor can flow through cross-cutting code
    /// that addresses users and agents uniformly without losing the typed <see cref="AgentId"/>.
    /// </summary>
    CitizenId ICitizen.Id => CitizenId.Of(AgentId);

    /// <summary>Optional emoji that visually identifies this agent in user interfaces.</summary>
    public string? Emoji { get; init; }

    /// <summary>Optional description of the agent's purpose.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Optional display order. Lower values sort first. When null the agent sorts
    /// alphabetically after all agents that have an explicit order value.
    /// </summary>
    public int? Order { get; init; }

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
    /// Role names this agent can converse with. Any agent whose <c>metadata.role</c> matches
    /// one of these values is authorized as a sub-agent, in addition to those listed in <see cref="SubAgentIds" />.
    /// </summary>
    public IReadOnlyList<string> SubAgentRoles { get; init; } = [];

    /// <summary>
    /// The isolation strategy to use when running this agent.
    /// Defaults to <c>"in-process"</c> if not specified.
    /// </summary>
    public string IsolationStrategy { get; init; } = "in-process";

    /// <summary>
    /// Prompt caching retention policy for this agent.
    /// When <see langword="null"/>, the provider default (<c>short</c>) is used.
    /// Valid values: <c>"none"</c>, <c>"short"</c>, <c>"long"</c>.
    /// Set to <c>"none"</c> to disable prompt caching for this agent.
    /// </summary>
    public string? CacheRetentionMode { get; init; }

    /// <summary>
    /// Agent-level default thinking (reasoning) level. This is the agent layer of the
    /// three-layer <c>ModelOverrideResolver</c> stack (model defaults -&gt; agent -&gt;
    /// conversation); when <see langword="null"/> the resolver falls through to the model
    /// default. Stored as the wire-form string of
    /// <c>BotNexus.Agent.Providers.Core.Models.ThinkingLevel</c>
    /// (<c>"minimal"</c>, <c>"low"</c>, <c>"medium"</c>, <c>"high"</c>, <c>"xhigh"</c>,
    /// <c>"max"</c>) rather than the enum itself so the Domain assembly keeps its zero
    /// non-framework dependency surface (same rationale as <see cref="CacheRetentionMode"/>).
    /// A value the selected model does not advertise as supported is rejected at
    /// registration time (REST and tool paths) and skipped when loaded from config.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Display(
        Name = "Thinking level",
        Description = "Default reasoning effort this agent requests, validated against the model's capabilities.",
        GroupName = "Agent",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "agent", Order = 4)]
    public string? Thinking { get; init; }

    /// <summary>
    /// Agent-level default context-window size (in tokens). This is the agent layer of the
    /// three-layer <c>ModelOverrideResolver</c> stack; when <see langword="null"/> the resolver
    /// falls through to the model default. Only sizes the selected model advertises as
    /// supported (via <c>ModelRegistry.GetSupportedContextSizes</c>) are accepted; an
    /// unsupported size is rejected at registration time and skipped when loaded from config.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Display(
        Name = "Context window",
        Description = "Default context-window size (tokens) this agent requests, validated against the model's capabilities.",
        GroupName = "Agent",
        Order = 5)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "agent", Order = 5)]
    public int? ContextWindow { get; init; }

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
    /// Whether this agent is a built-in platform agent (determined by <c>metadata.builtin</c>).
    /// Built-in agents sort after user-configured agents in portal UI.
    /// </summary>
    public bool IsBuiltIn => Metadata.TryGetValue("builtin", out var v) && v is true;

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

    /// <summary>
    /// Datetime injection configuration for this agent.
    /// When non-null, overrides the world-level <see cref="GatewaySettingsConfig.DateTimeInjection"/> setting.
    /// </summary>
    public DateTimeInjectionConfig? DateTimeInjection { get; init; }

    /// <summary>Session access level for this agent's session tool. Defaults to "own".</summary>
    public string SessionAccessLevel { get; init; } = "own";

    /// <summary>File system access policy for this agent. Null means workspace-only access.</summary>
    public FileAccessPolicy? FileAccess { get; init; }

    /// <summary>Agent IDs this agent can view sessions for (when SessionAccessLevel is "allowlist").</summary>
    public IReadOnlyList<string> SessionAllowedAgents { get; init; } = [];

    /// <summary>Conversation access level for this agent's conversation tool. Defaults to "own".</summary>
    public string ConversationAccessLevel { get; init; } = "own";

    /// <summary>Agent IDs this agent can view conversations for (when ConversationAccessLevel is "allowlist").</summary>
    public IReadOnlyList<string> ConversationAllowedAgents { get; init; } = [];

    /// <summary>
    /// Extension-specific configuration keyed by extension ID.
    /// Extensions read their own config from this bag using their ID as the key.
    /// Example: <c>ExtensionConfig["botnexus-skills"]</c> for skills config.
    /// </summary>
    public IReadOnlyDictionary<string, System.Text.Json.JsonElement> ExtensionConfig { get; init; } =
        new Dictionary<string, System.Text.Json.JsonElement>();
/// <summary>Conversation retention policy override for this agent. Null means world default applies.</summary>
    public AgentConversationRetentionConfig? ConversationRetention { get; init; }

    /// <summary>
    /// Custom shell command array for this agent. Overrides the gateway-level ShellCommand.
    /// Element [0] is the executable, remaining elements are base arguments.
    /// The agent's command string is appended as the final argument.
    /// </summary>
    public string[]? ShellCommand { get; init; }
}
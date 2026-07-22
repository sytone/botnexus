using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Domain;
using BotNexus.Domain.World;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Platform-wide BotNexus configuration stored at ~/.botnexus/config.json.
/// </summary>
public sealed class PlatformConfig : IValidatableObject
{
    /// <summary>Optional JSON schema reference for editor IntelliSense/validation.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    /// <summary>Configuration schema version for forward compatibility.</summary>
    /// <remarks>
    /// Named "PlatformVersion" (not "Version") to avoid collision with the DOTNET_VERSION
    /// environment variable which the .NET host prefix-strips to just "VERSION" in IConfiguration.
    /// JsonPropertyName keeps config.json reading from the "version" key.
    /// </remarks>
    [JsonPropertyName("version")]
    [Display(
        Name = "Config schema version",
        Description = "Configuration schema version for forward compatibility. Bumped only when the config shape changes incompatibly.",
        GroupName = "General",
        Order = 0)]
    [DefaultValue(1)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "general", Order = 0)]
    public int PlatformVersion { get; set; } = 1;

    /// <summary>Gateway-specific settings.</summary>
    public GatewaySettingsConfig? Gateway { get; set; }

    /// <summary>Agent definitions keyed by agent ID.</summary>
    public Dictionary<string, AgentDefinitionConfig>? Agents { get; set; }

    /// <summary>Provider configurations keyed by provider name.</summary>
    public Dictionary<string, ProviderConfig>? Providers { get; set; }

    /// <summary>Channel settings keyed by channel name.</summary>
    public Dictionary<string, ChannelConfig>? Channels { get; set; }

    /// <summary>API key for Gateway authentication (null = dev mode, no auth).</summary>
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "general", Order = 1, Secret = true)]
    public string? ApiKey { get; set; }

    /// <summary>Cron scheduler settings and optional seed jobs.</summary>
    public CronConfig? Cron { get; set; }

    /// <summary>Named prompt templates for CLI rendering and cron template resolution.</summary>
    public Dictionary<string, PromptTemplateConfig>? PromptTemplates { get; set; }

    /// <summary>
    /// Workspace and portal display settings (reports, file viewer limits).
    /// </summary>
    public WorkspacePortalConfig? Workspace { get; set; }

    /// <summary>
    /// World-level agent defaults. Populated at load time from the <c>agents.defaults</c> reserved key.
    /// Not directly serialized — extracted separately from the agents dictionary.
    /// </summary>
    [JsonIgnore]
    public AgentDefaultsConfig? AgentDefaults { get; set; }

    /// <summary>
    /// Raw JSON elements for each agent, keyed by agent ID. Used for presence-aware field-level merge.
    /// Populated at load time alongside <see cref="AgentDefaults" />.
    /// </summary>
    [JsonIgnore]
    public Dictionary<string, System.Text.Json.JsonElement>? AgentRawElements { get; set; }

    /// <summary>
    /// Cross-field and graph validation escape hatch for the DataAnnotations pipeline (#1613,
    /// config parity PBI 5/6 of #1579). Per-field scalar rules live as DataAnnotations attributes
    /// (for example <see cref="System.ComponentModel.DataAnnotations.RangeAttribute"/>) directly on
    /// the model and are enforced by <see cref="System.ComponentModel.DataAnnotations.Validator.TryValidateObject"/>.
    /// Rules that span multiple fields, iterate user-keyed dictionaries, or apply conditional
    /// "if X set then Y required" logic cannot be expressed as per-field attributes, so they are
    /// retained imperatively in <see cref="PlatformConfigLoader"/> and surfaced here so a single
    /// <c>TryValidateObject</c> pass enforces both layers identically server-side.
    /// </summary>
    /// <param name="validationContext">The DataAnnotations validation context (unused; the whole
    /// graph is validated from this root instance).</param>
    /// <returns>One <see cref="ValidationResult"/> per cross-field rule violation, with the same
    /// message text the legacy imperative validator produced.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var error in PlatformConfigLoader.CollectCrossFieldErrors(this))
            yield return new ValidationResult(error);
    }

}

/// <summary>Provider-specific configuration.</summary>
public sealed class ProviderConfig
{
    /// <summary>Whether this provider is enabled. Disabled providers are hidden from API.</summary>
    [Display(
        Name = "Enabled",
        Description = "Whether this provider is enabled. Disabled providers are hidden from the API.",
        GroupName = "Provider",
        Order = 0)]
    [DefaultValue(true)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "provider", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>API key or reference to auth.json entry.</summary>
    [Display(
        Name = "API key",
        Description = "API key for this provider, or a reference to an auth.json entry. Sensitive: stored and shown masked.",
        GroupName = "Provider",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "provider", Order = 1, Secret = true)]
    public string? ApiKey { get; set; }

    /// <summary>Base URL override.</summary>
    [Display(
        Name = "Base URL",
        Description = "Optional base URL override for this provider's API endpoint.",
        GroupName = "Provider",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "provider", Order = 2)]
    public string? BaseUrl { get; set; }

    /// <summary>Default model for this provider.</summary>
    [Display(
        Name = "Default model",
        Description = "Default model identifier used for this provider when an agent does not specify one.",
        GroupName = "Provider",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "provider", Order = 3, OptionsSource = "models")]
    public string? DefaultModel { get; set; }

    /// <summary>Allowed model IDs for this provider. Null means all models, empty means none.</summary>
    public List<string>? Models { get; set; }

    /// <summary>
    /// Optional API identifier used when registering models from this provider's <see cref="Models"/>
    /// list. Defaults to <c>"openai-completions"</c> for backward compatibility with config-driven
    /// OpenAI-compatible endpoints (Ollama, LM Studio, etc.). Set to <c>"integration-mock"</c> or
    /// another registered provider's API name to register models against a different
    /// <see cref="BotNexus.Agent.Providers.Core.Registry.IApiProvider"/>.
    /// </summary>
    public string? Api { get; set; }

    /// <summary>
    /// PBI6 (#1707): explicit reasoning/thinking capability for models registered from this
    /// provider's <see cref="Models"/> list. When null (the default) the capability is inferred
    /// from each model's family so a known reasoning family (Claude 4+, GPT-5+, o3/o4, Gemini 3+,
    /// Grok-code) is picked up automatically; set it explicitly for a local model whose id the
    /// family heuristic does not recognise.
    /// </summary>
    public bool? Reasoning { get; set; }

    /// <summary>
    /// PBI6 (#1707): explicit extra-high (ExtraHigh / Max) thinking-tier capability for this
    /// provider's dynamic models. When null the value is inferred from the model family. Ignored
    /// (clamped off) for a model that does not support reasoning.
    /// </summary>
    public bool? SupportsExtraHighThinking { get; set; }

    /// <summary>
    /// PBI6 (#1707): explicit extended (1M) context-window capability for this provider's dynamic
    /// models. When null the value is inferred from the model family (Anthropic-direct Claude
    /// Sonnet 4/4.5 and Opus 4.5). Drives the context-size picker's second (1M) tier.
    /// </summary>
    public bool? SupportsExtendedContextWindow { get; set; }

    /// <summary>
    /// PBI6 (#1707): default context-window size (in tokens) for this provider's dynamic models.
    /// When null a conservative 128000-token default is used. Sets the standard tier the
    /// context-size picker offers for a config-declared model.
    /// </summary>
    public int? ContextWindow { get; set; }
}

/// <summary>Gateway runtime configuration.</summary>
public sealed class GatewaySettingsConfig
{
    /// <summary>Gateway HTTP listen URL.</summary>
    [Display(
        Name = "Listen URL",
        Description = "HTTP(S) URL the gateway binds to (for example http://localhost:5005). Supports Kestrel wildcards such as http://+:5000.",
        GroupName = "Gateway",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "gateway", Order = 0)]
    public string? ListenUrl { get; set; }
    /// <summary>Default agent to route to when none specified.</summary>
    [Display(
        Name = "Default agent",
        Description = "Agent ID to route to when an incoming message does not specify one.",
        GroupName = "Gateway",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "gateway", Order = 1)]
    public string? DefaultAgentId { get; set; }
    /// <summary>Path to agents configuration directory.</summary>
    public string? AgentsDirectory { get; set; }
    /// <summary>Path to sessions storage directory.</summary>
    public string? SessionsDirectory { get; set; }
    /// <summary>Session store selection and configuration.</summary>
    public SessionStoreConfig? SessionStore { get; set; }

    /// <summary>Interval in minutes between periodic PASSIVE SQLite WAL checkpoints (#1438). Default 30.</summary>
    [Display(
        Name = "WAL checkpoint interval (min)",
        Description = "Minutes between periodic PASSIVE SQLite WAL checkpoints. A TRUNCATE checkpoint also runs on graceful shutdown. Default 30.",
        GroupName = "Gateway",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "gateway", Order = 3)]
    public int? WalCheckpointIntervalMinutes { get; set; }
    /// <summary>Trusted sub-agent spawning limits and per-parent budget overrides.</summary>
    public SubAgentOptions? SubAgents { get; set; }
    /// <summary>Session compaction settings.</summary>
    public CompactionOptions? Compaction { get; set; }
    /// <summary>Write-time cap on the size of individual tool results persisted to session history (#1598).</summary>
    public ToolResultPersistenceConfig? ToolResultPersistence { get; set; }
    /// <summary>Post-turn claim auditor (anti-fabrication) settings (#1600).</summary>
    public ClaimAuditConfig? ClaimAudit { get; set; }
    /// <summary>CORS settings for browser-based clients.</summary>
    public CorsConfig? Cors { get; set; }
    /// <summary>Per-client request rate limiting settings.</summary>
    public RateLimitConfig? RateLimit { get; set; }
    /// <summary>Explicit SignalR hub transport limits (frame size, parallel invocations, stream buffer).</summary>
    public SignalRConfig? SignalR { get; set; }
    /// <summary>Logging level override.</summary>
    [Display(
        Name = "Log level",
        Description = "Minimum log level override. One of Trace, Debug, Information, Warning, Error, Critical.",
        GroupName = "Gateway",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "gateway", Order = 2)]
    public string? LogLevel { get; set; }
    /// <summary>Multi-tenant API keys keyed by key ID.</summary>
    public Dictionary<string, ApiKeyConfig>? ApiKeys { get; set; }
    /// <summary>Extensions loading settings.</summary>
    public ExtensionsConfig? Extensions { get; set; }
    /// <summary>World identity shown by gateway clients.</summary>
    public WorldIdentity? World { get; set; }
    /// <summary>Named locations registry for resource management.</summary>
    public Dictionary<string, LocationConfig>? Locations { get; set; }
    /// <summary>Optional explicit cross-world communication permissions.</summary>
    public List<CrossWorldPermissionConfig>? CrossWorldPermissions { get; set; }
    /// <summary>Cross-world federation settings for gateway-to-gateway communication.</summary>
    public CrossWorldFederationConfig? CrossWorld { get; set; }
    /// <summary>Default file access policy applied to all agents unless overridden per-agent.</summary>
    public FileAccessPolicyConfig? FileAccess { get; set; }
    /// <summary>
    /// Preferred shell for command execution on Windows.
    /// Values: <c>"auto"</c> (default — bash when available, PowerShell fallback),
    /// <c>"pwsh"</c> (always PowerShell), <c>"bash"</c> (always bash).
    /// Ignored on non-Windows platforms where bash is always used.
    /// </summary>
    public string? ShellPreference { get; set; }

    /// <summary>
    /// Custom shell command array for command execution.
    /// Element [0] is the executable, remaining elements are base arguments.
    /// The agent's command string is appended as the final argument.
    /// Example: <c>["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command"]</c>.
    /// When set, overrides <see cref="ShellPreference"/> entirely.
    /// </summary>
    public string[]? ShellCommand { get; set; }

    /// <summary>Auto-update settings for self-updating the gateway via the BotNexus CLI.</summary>
    public AutoUpdateConfig? AutoUpdate { get; set; }

    /// <summary>
    /// Auxiliary (cheap/fast) model configuration for background gateway tasks.
    /// Currently used for: conversation title generation.
    /// </summary>
    public AuxiliaryConfig? Auxiliary { get; set; }

    /// <summary>
    /// Server-wide default IANA timezone ID used when an agent has no Soul timezone configured.
    /// Falls back to UTC when null or invalid.
    /// Example: <c>"America/Los_Angeles"</c>.
    /// </summary>
    [Display(
        Name = "Default timezone",
        Description = "Server-wide default IANA timezone ID used when an agent has no timezone configured. Falls back to UTC when blank or invalid.",
        GroupName = "Gateway",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "gateway", Order = 3)]
    public string? DefaultTimezone { get; set; }

    /// <summary>
    /// When true, all provider HTTP requests and responses are logged at Debug level.
    /// Auth headers are always redacted. Response bodies are not buffered for streaming calls.
    /// Off by default; enable only for debugging unexpected provider responses.
    /// </summary>
    [Display(
        Name = "Log provider requests",
        Description = "When on, all provider HTTP requests and responses are logged at Debug level. Auth headers are always redacted. For debugging only.",
        GroupName = "Gateway",
        Order = 4)]
    [DefaultValue(false)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "gateway", Order = 4)]
    public bool EnableProviderRequestLogging { get; set; } = false;

    /// <summary>
    /// Server-wide datetime injection settings. When enabled, the current datetime is prepended
    /// to every user message sent to the LLM so agents always know the current time.
    /// Per-agent overrides take precedence over this world default.
    /// </summary>
    public DateTimeInjectionConfig? DateTimeInjection { get; set; }

    /// <summary>
    /// Registered satellite nodes keyed by satellite ID.
    /// Satellites are remote persistent processes that connect to the gateway for
    /// notifications, canvas rendering, and optionally remote command execution.
    /// </summary>
    public Dictionary<string, SatelliteConfig>? Satellites { get; set; }
}

/// <summary>
/// Write-time tool-result size cap (#1598). Large tool results (e.g. a recursive directory
/// listing or a session-history dump) are otherwise persisted into <c>session_history</c> at
/// full size and re-sent to the model on every subsequent turn, consuming context budget with
/// zero ongoing value. When enabled, a result exceeding <see cref="MaxBytes"/> UTF-8 bytes is
/// truncated at write time (on a rune boundary) with an explicit <c>[truncated N bytes]</c> marker.
/// </summary>
public sealed class ToolResultPersistenceConfig
{
    /// <summary>
    /// Whether the write-time tool-result cap is enabled. Defaults to <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum UTF-8 byte size of a single persisted tool result. Results larger than this are
    /// truncated at write time with a <c>[truncated N bytes]</c> marker. Defaults to 16384 (16 KiB).
    /// A value of 0 or less disables truncation even when <see cref="Enabled"/> is true.
    /// </summary>
    public int MaxBytes { get; set; } = 16_384;
}

/// <summary>
/// Configuration for the post-turn claim auditor (#1600, control #1 of #1551). The auditor scans
/// the agent's final user-facing message for artifact-shaped claims (a GitHub issue was filed, a PR
/// opened, a file written, something sent/deployed, an audit "verified") and flags any claim that
/// has no backing tool call among the tools actually invoked during the run. This inverts the
/// trust model that failed when an agent narrated "filed issue #N" with no tool call that turn:
/// it verifies rather than trusting narration.
/// </summary>
public sealed class ClaimAuditConfig
{
    /// <summary>
    /// Whether the post-turn claim auditor runs. Defaults to <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Reaction on detecting an unbacked claim: <c>"warn"</c> (emit an observable signal only,
    /// the safe default) or <c>"block"</c> (also mark the turn as one that should be blocked).
    /// Unrecognised values fall back to <c>"warn"</c>.
    /// </summary>
    public string Mode { get; set; } = "warn";
}

/// <summary>
/// Configuration for the auto-update feature that polls GitHub and spawns the CLI updater.
/// When <see cref="Enabled"/> is true, both <see cref="CliPath"/> and <see cref="SourcePath"/>
/// must be provided or <c>POST /api/gateway/update/start</c> will return 412.
/// </summary>
public sealed class AutoUpdateConfig
{
    /// <summary>Enables background GitHub polling and the update endpoint. Defaults to false.</summary>
    [Display(
        Name = "Enable auto-update",
        Description = "Enables background GitHub polling and the self-update endpoint.",
        GroupName = "Auto-update",
        Order = 0)]
    [DefaultValue(false)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "auto-update", Order = 0)]
    public bool Enabled { get; set; } = false;

    /// <summary>How often to poll GitHub for a new commit. Minimum 5. Defaults to 60.</summary>
    [Display(
        Name = "Check interval (minutes)",
        Description = "How often to poll GitHub for a new commit, in minutes. Minimum 5.",
        GroupName = "Auto-update",
        Order = 1)]
    [DefaultValue(60)]
    [Range(5, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "auto-update", Order = 1)]
    public int CheckIntervalMinutes { get; set; } = 60;

    /// <summary>GitHub repository owner. Defaults to <c>sytone</c>.</summary>
    public string RepositoryOwner { get; set; } = "sytone";

    /// <summary>GitHub repository name. Defaults to <c>botnexus</c>.</summary>
    public string RepositoryName { get; set; } = "botnexus";

    /// <summary>Branch to track. Defaults to <c>main</c>.</summary>
    public string Branch { get; set; } = "main";

    /// <summary>
    /// Absolute path to the BotNexus CLI entry point used to run the update.
    /// Required when <see cref="Enabled"/> is true.
    /// If the path ends with <c>.dll</c> it is launched via <c>dotnet</c>; otherwise it is run directly.
    /// </summary>
    public string? CliPath { get; set; }

    /// <summary>
    /// Absolute path to the BotNexus source tree. Passed to the CLI update command as <c>--source</c>.
    /// Required when <see cref="Enabled"/> is true.
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Update channel to forward to the CLI update command. Typical values: <c>stable</c>, <c>beta</c>, <c>dev</c>.
    /// When null or empty the CLI default channel is used.
    /// </summary>
    public string? Channel { get; set; }

    /// <summary>Seconds to wait after returning 202 before calling StopApplication(). Minimum 1. Defaults to 2.</summary>
    public int ShutdownDelaySeconds { get; set; } = 2;
}

/// <summary>Named location configuration for resource access.</summary>
public sealed class LocationConfig
{
    /// <summary>Location type: filesystem, api, mcp-server, database, remote-node.</summary>
    public string Type { get; set; } = "filesystem";

    /// <summary>Path for filesystem locations.</summary>
    public string? Path { get; set; }

    /// <summary>Endpoint URL for api/mcp-server/remote-node locations.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Connection string for database locations.</summary>
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "location", Order = 3, Secret = true)]
    public string? ConnectionString { get; set; }

    /// <summary>Human-readable description.</summary>
    public string? Description { get; set; }

    /// <summary>Extensible properties.</summary>
    public Dictionary<string, string>? Properties { get; set; }
}

/// <summary>Configuration for granting communication with another world.</summary>
public sealed class CrossWorldPermissionConfig
{
    /// <summary>Identifier of the target world this permission applies to.</summary>
    public string? TargetWorldId { get; set; }

    /// <summary>Specific agents allowed to communicate. Null means all hosted agents.</summary>
    public List<string>? AllowedAgents { get; set; }

    /// <summary>Whether inbound communication from the target world is allowed.</summary>
    public bool AllowInbound { get; set; } = true;

    /// <summary>Whether outbound communication to the target world is allowed.</summary>
    public bool AllowOutbound { get; set; } = true;
}

/// <summary>Cross-world federation runtime configuration.</summary>
public sealed class CrossWorldFederationConfig
{
    /// <summary>Known peer gateways keyed by world ID or alias.</summary>
    public Dictionary<string, CrossWorldPeerConfig>? Peers { get; set; }

    /// <summary>Inbound cross-world relay policy.</summary>
    public CrossWorldInboundConfig? Inbound { get; set; }

    /// <summary>Optional explicit cross-world agent discovery map.</summary>
    public Dictionary<string, CrossWorldAgentConfig>? Agents { get; set; }
}

/// <summary>Outbound peer gateway settings.</summary>
public sealed class CrossWorldPeerConfig
{
    /// <summary>Canonical world ID for this peer (defaults to dictionary key).</summary>
    public string? WorldId { get; set; }

    /// <summary>Peer gateway endpoint URL.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Shared API key used for gateway-to-gateway relay authentication.</summary>
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "cross-world-peer", Order = 2, Secret = true)]
    public string? ApiKey { get; set; }

    /// <summary>Whether this peer is enabled for outbound calls.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Inbound cross-world relay authentication and allow-list policy.</summary>
public sealed class CrossWorldInboundConfig
{
    /// <summary>Whether inbound relay endpoint is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Allowed source world IDs. Empty means no source worlds are allowed.</summary>
    public List<string>? AllowedWorlds { get; set; }

    /// <summary>Shared API keys keyed by source world ID.</summary>
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "cross-world-inbound", Order = 2, Secret = true)]
    public Dictionary<string, string>? ApiKeys { get; set; }
}

/// <summary>Explicit cross-world agent discovery record.</summary>
public sealed class CrossWorldAgentConfig
{
    /// <summary>Target world hosting the agent.</summary>
    public string? WorldId { get; set; }

    /// <summary>Remote agent ID within the target world.</summary>
    public string? AgentId { get; set; }

    /// <summary>Optional operator-facing description.</summary>
    public string? Description { get; set; }
}

/// <summary>CORS settings for gateway HTTP endpoints.</summary>
public sealed class CorsConfig
{
    /// <summary>Explicit origins allowed to access the gateway from browsers.</summary>
    public List<string>? AllowedOrigins { get; set; }
}

/// <summary>Rate limiting settings for gateway HTTP endpoints.</summary>
public sealed class RateLimitConfig
{
    /// <summary>Whether rate limiting is active. Defaults to false (disabled).</summary>
    [Display(
        Name = "Enable rate limiting",
        Description = "Whether per-client request rate limiting is active.",
        GroupName = "Rate limit",
        Order = 0)]
    [DefaultValue(false)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "rate-limit", Order = 0)]
    public bool Enabled { get; set; }

    /// <summary>Maximum requests allowed in a window for a single client.</summary>
    [Display(
        Name = "Requests per minute",
        Description = "Maximum requests allowed within a window for a single client.",
        GroupName = "Rate limit",
        Order = 1)]
    [DefaultValue(300)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "rate-limit", Order = 1)]
    public int RequestsPerMinute { get; set; } = 300;

    /// <summary>Window size in seconds used for request counting.</summary>
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum number of distinct client windows retained in memory. Bounds the per-client
    /// tracking dictionary so a flood of distinct client keys cannot drive the gateway to
    /// memory exhaustion (a DoS against the DoS-protection itself). When the cap is reached,
    /// stale entries are pruned first, then a window that is not actively rate-limiting a
    /// client is evicted; if none can be freed, the new request is rejected with 429 rather
    /// than inserting. Windows actively counting toward a 429 are never evicted, so a flood
    /// cannot clear an attacker's own throttle. A non-positive value disables the cap.
    /// </summary>
    public int MaxEntries { get; set; } = 10_000;
}

/// <summary>
/// Explicit SignalR hub transport limits. When this section is absent, secure defaults are
/// applied (see <c>SignalRHubLimits</c>) rather than the framework's implicit values, so the
/// gateway always bounds inbound frame size and per-connection concurrency intentionally.
/// </summary>
public sealed class SignalRConfig
{
    /// <summary>
    /// Maximum size, in bytes, of a single inbound hub message. Must accommodate base64-encoded
    /// inline media (which exceeds the framework's 32 KB default) while bounding runaway frames.
    /// Non-positive values fall back to the secure default.
    /// </summary>
    public long? MaximumReceiveMessageSizeBytes { get; set; }

    /// <summary>
    /// Maximum number of hub method invocations a single connection may run in parallel.
    /// Bounds concurrent work a client can force on the server. Non-positive values fall back to
    /// the secure default.
    /// </summary>
    public int? MaximumParallelInvocationsPerClient { get; set; }

    /// <summary>
    /// Maximum number of items buffered for client upload streams before processing blocks.
    /// Non-positive values fall back to the secure default.
    /// </summary>
    public int? StreamBufferCapacity { get; set; }

    /// <summary>
    /// Interval, in seconds, at which the server sends keep-alive pings to idle clients (#1840).
    /// Chosen to sit comfortably under the netbird tunnel idle-cutoff so a quiet mobile connection
    /// never idles the tunnel out mid-session. Non-positive values fall back to the mobile-tuned
    /// default. The server timeout (<see cref="ClientTimeoutIntervalSeconds"/>) is always coerced
    /// to at least twice this value so a single dropped ping cannot terminate the connection.
    /// </summary>
    public int? KeepAliveIntervalSeconds { get; set; }

    /// <summary>
    /// Interval, in seconds, after which the server considers a client dead if no message or ping
    /// has arrived (#1840). Widened over the framework's 30s default to tolerate the jitter and
    /// brief stalls of a mobile link tunnelled through netbird. Must be at least twice
    /// <see cref="KeepAliveIntervalSeconds"/>; smaller (or non-positive) values are coerced up to
    /// the mobile-tuned default so a misconfig cannot make the server hang up prematurely.
    /// </summary>
    public int? ClientTimeoutIntervalSeconds { get; set; }
}

/// <summary>Cron scheduler configuration.</summary>
public sealed class CronConfig
{
    /// <summary>Whether the cron scheduler is enabled.</summary>
    [Display(
        Name = "Enable cron",
        Description = "Whether the cron scheduler runs scheduled jobs.",
        GroupName = "Cron",
        Order = 0)]
    [DefaultValue(true)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "cron", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>Scheduler polling interval in seconds.</summary>
    [Display(
        Name = "Tick interval (seconds)",
        Description = "How often the scheduler wakes to evaluate due jobs, in seconds.",
        GroupName = "Cron",
        Order = 1)]
    [DefaultValue(60)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "cron", Order = 1)]
    public int TickIntervalSeconds { get; set; } = 60;

    /// <summary>Optional job definitions keyed by stable job ID.</summary>
    public Dictionary<string, CronJobConfig>? Jobs { get; set; }
}

/// <summary>Config-defined cron job descriptor.</summary>
public sealed class CronJobConfig
{
    /// <summary>Display name for the cron job.</summary>
    public string? Name { get; set; }

    /// <summary>Cron expression schedule.</summary>
    public string? Schedule { get; set; }

    /// <summary>Action type (for example: <c>agent-prompt</c>).</summary>
    public string? ActionType { get; set; }

    /// <summary>Target agent identifier for agent prompt jobs.</summary>
    public string? AgentId { get; set; }

    /// <summary>Prompt message for agent prompt jobs.</summary>
    public string? Message { get; set; }

    /// <summary>Named prompt template for agent prompt jobs.</summary>
    public string? TemplateName { get; set; }

    /// <summary>Template parameter values used when rendering <see cref="TemplateName" />.</summary>
    public Dictionary<string, string>? TemplateParameters { get; set; }

    /// <summary>Optional model override for agent prompt jobs.</summary>
    public string? Model { get; set; }

    /// <summary>Webhook destination URL for webhook jobs.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Shell command payload for shell jobs.</summary>
    public string? ShellCommand { get; set; }

    /// <summary>Whether this job is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional creator label for auditing.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Optional metadata entries persisted with the job.</summary>
    public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary>Named prompt template descriptor.</summary>
public sealed class PromptTemplateConfig
{
    /// <summary>Template body with <c>{{parameter}}</c> placeholders.</summary>
    public string? Prompt { get; set; }

    /// <summary>Optional human-friendly description.</summary>
    public string? Description { get; set; }

    /// <summary>Default values for template parameters.</summary>
    public Dictionary<string, string>? Defaults { get; set; }

    /// <summary>Optional per-parameter metadata and defaults.</summary>
    public Dictionary<string, PromptTemplateParameterConfig>? Parameters { get; set; }
}

/// <summary>Prompt template parameter configuration.</summary>
public sealed class PromptTemplateParameterConfig
{
    /// <summary>Optional parameter description.</summary>
    public string? Description { get; set; }

    /// <summary>Optional default value.</summary>
    public string? Default { get; set; }

    /// <summary>Whether the parameter must be supplied if no default exists.</summary>
    public bool Required { get; set; }
}

/// <summary>Configuration for dynamic extension discovery and loading.</summary>
public sealed class ExtensionsConfig
{
    /// <summary>
    /// Root directory containing extension folders with botnexus-extension.json manifests.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Enables or disables dynamic extension loading.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// World-level default extension configuration, keyed by extension ID.
    /// Deep-merged with agent-level overrides to produce effective config per agent.
    /// </summary>
    public Dictionary<string, JsonElement>? Defaults { get; set; }
}

/// <summary>Agent definition in platform config.</summary>
public sealed class AgentDefinitionConfig
{
    /// <summary>Provider name (e.g. 'copilot').</summary>
    public string? Provider { get; set; }
    /// <summary>Human-readable display name.</summary>
    [Display(
        Name = "Display name",
        Description = "Human-readable display name shown for this agent in clients.",
        GroupName = "Agent",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "agent", Order = 1)]
    public string? DisplayName { get; set; }
    /// <summary>Optional emoji shown alongside the agent name in clients.</summary>
    public string? Emoji { get; set; }
    /// <summary>Description of the agent's purpose.</summary>
    public string? Description { get; set; }
    /// <summary>Model identifier (e.g. 'gpt-4.1').</summary>
    [Display(
        Name = "Model",
        Description = "Model identifier this agent uses (for example gpt-4.1).",
        GroupName = "Agent",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "agent", Order = 2)]
    public string? Model { get; set; }
    /// <summary>Model IDs this agent is allowed to use. Null means unrestricted within provider allowlist.</summary>
    public List<string>? AllowedModels { get; set; }
    /// <summary>Ordered list of files to load as the system prompt. Empty = default order.</summary>
    public List<string>? SystemPromptFiles { get; set; }
    /// <summary>Path to a single system prompt file (legacy, prefer SystemPromptFiles).</summary>
    public string? SystemPromptFile { get; set; }
    /// <summary>Tool identifiers this agent has access to.</summary>
    public List<string>? ToolIds { get; set; }
    /// <summary>Per-tool timeout in seconds for runtime tool execution safety caps.</summary>
    public int? ToolTimeoutSeconds { get; set; }
    /// <summary>Agent IDs this agent can call as sub-agents.</summary>
    public List<string>? SubAgents { get; set; }
    /// <summary>Role names this agent can converse with (role-based grants for agent_converse).</summary>
    public List<string>? SubAgentRoles { get; set; }
    /// <summary>Isolation strategy name (e.g. 'in-process').</summary>
    public string? IsolationStrategy { get; set; }
    /// <summary>Prompt caching retention policy for this agent. Null means provider default (short) is used.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<BotNexus.Agent.Providers.Core.Models.CacheRetention>))]
    public BotNexus.Agent.Providers.Core.Models.CacheRetention? CacheRetention { get; set; }
    /// <summary>
    /// Agent-level default thinking (reasoning) level. Agent layer of the three-layer
    /// model/thinking/context override stack consumed by <c>ModelOverrideResolver</c>.
    /// Null means "unset - inherit the model default". Validated against the selected
    /// model's advertised capabilities when the descriptor is built; an unsupported value
    /// causes the agent to be skipped at config load with a warning.
    /// </summary>
    [Display(
        Name = "Thinking level",
        Description = "Default reasoning effort this agent requests (validated against the model's capabilities).",
        GroupName = "Agent",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "agent", Order = 4)]
    public string? Thinking { get; set; }
    /// <summary>
    /// Agent-level default context-window size in tokens. Agent layer of the three-layer
    /// override stack. Null means "unset - inherit the model default". Validated against the
    /// selected model's advertised context sizes; an unsupported value causes the agent to be
    /// skipped at config load with a warning.
    /// </summary>
    [Display(
        Name = "Context window",
        Description = "Default context-window size (tokens) this agent requests (validated against the model's capabilities).",
        GroupName = "Agent",
        Order = 5)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "agent", Order = 5)]
    public int? ContextWindow { get; set; }
    /// <summary>Maximum concurrent sessions for this agent.</summary>
    public int? MaxConcurrentSessions { get; set; }
    /// <summary>Agent-level metadata.</summary>
    public JsonElement? Metadata { get; set; }
    /// <summary>Strategy-specific isolation options.</summary>
    public JsonElement? IsolationOptions { get; set; }
    /// <summary>Whether this agent is enabled.</summary>
    [Display(
        Name = "Enabled",
        Description = "Whether this agent is enabled and available for routing.",
        GroupName = "Agent",
        Order = 3)]
    [DefaultValue(true)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "agent", Order = 3)]
    public bool Enabled { get; set; } = true;
    /// <summary>Memory system configuration for this agent.</summary>
    public MemoryAgentConfig? Memory { get; set; }
    /// <summary>Soul session lifecycle configuration for this agent.</summary>
    public SoulAgentConfig? Soul { get; set; }
    /// <summary>Heartbeat polling configuration.</summary>
    public HeartbeatAgentConfig? Heartbeat { get; set; }
    /// <summary>Datetime injection configuration override for this agent. Overrides world default when set.</summary>
    public DateTimeInjectionConfig? DateTimeInjection { get; set; }
    /// <summary>Session access configuration for this agent's session tool.</summary>
    public SessionAccessConfig? SessionAccess { get; set; }
    /// <summary>Conversation access configuration for this agent's conversation tool.</summary>
    public ConversationAccessConfig? ConversationAccess { get; set; }
    /// <summary>File access policy for this agent's file tools.</summary>
    public FileAccessPolicyConfig? FileAccess { get; set; }

    /// <summary>
    /// Custom shell command array for this agent. Overrides the gateway-level ShellCommand.
    /// Element [0] is the executable, remaining elements are base arguments.
    /// The agent's command string is appended as the final argument.
    /// </summary>
    public string[]? ShellCommand { get; set; }

    /// <summary>
    /// Extension-specific configuration keyed by extension ID.
    /// Each extension reads its own section (e.g., "botnexus-skills", "botnexus-exec").
    /// </summary>
    public Dictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>Tool policy overrides for this agent.</summary>
    public ToolPolicyConfig? ToolPolicy { get; set; }

    /// <summary>
    /// Optional. Kind of agent — currently only <c>Named</c> is accepted from config.
    /// <c>SubAgent</c> is rejected by <c>AgentDescriptorValidator.ValidateForConfig</c>;
    /// sub-agents are runtime-only and produced exclusively by
    /// <c>DefaultSubAgentManager.SpawnAsync</c>. Omit the field entirely on existing
    /// configs; the default is <c>Named</c>.
    /// </summary>
    public AgentKind? Kind { get; set; }
}

/// <summary>Per-agent file access policy configuration.</summary>
public sealed class FileAccessPolicyConfig
{
    /// <summary>Paths the agent can read (exact paths or glob patterns).</summary>
    public List<string>? AllowedReadPaths { get; set; }

    /// <summary>Paths the agent can write (exact paths or glob patterns).</summary>
    public List<string>? AllowedWritePaths { get; set; }

    /// <summary>Paths explicitly denied even if otherwise allowed.</summary>
    public List<string>? DeniedPaths { get; set; }
}

/// <summary>Per-agent tool policy configuration that overrides default risk classifications.</summary>
public sealed class ToolPolicyConfig
{
    /// <summary>Tools that always require approval regardless of default classification.</summary>
    public List<string>? AlwaysApprove { get; set; }

    /// <summary>Tools that skip approval even if classified as dangerous (trusted).</summary>
    public List<string>? NeverApprove { get; set; }

    /// <summary>Tools completely blocked for this agent.</summary>
    public List<string>? Denied { get; set; }
}

/// <summary>Controls what sessions an agent can access via the session tool.</summary>
public sealed class SessionAccessConfig
{
    /// <summary>Access level: "own" (default), "allowlist", or "all".</summary>
    public string Level { get; set; } = "own";
    /// <summary>Agent IDs this agent can view sessions for (when level is "allowlist").</summary>
    public List<string>? AllowedAgents { get; set; }
}

/// <summary>Controls what conversations an agent can access via the conversation tool.</summary>
public sealed class ConversationAccessConfig
{
    /// <summary>Access level: "own" (default), "allowlist", or "all".</summary>
    public string Level { get; set; } = "own";
    /// <summary>Agent IDs this agent can view conversations for (when level is "allowlist").</summary>
    public List<string>? AllowedAgents { get; set; }
}

/// <summary>Channel definition in platform config.</summary>
public sealed class ChannelConfig
{
    /// <summary>Channel type (e.g. 'signalr', 'slack').</summary>
    public string? Type { get; set; }
    /// <summary>Whether this channel is enabled.</summary>
    [Display(
        Name = "Enabled",
        Description = "Whether this channel is enabled.",
        GroupName = "Channel",
        Order = 1)]
    [DefaultValue(true)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "channel", Order = 1)]
    public bool Enabled { get; set; } = true;
    /// <summary>Adapter-specific settings.</summary>
    public Dictionary<string, string>? Settings { get; set; }
}

/// <summary>Session store implementation configuration.</summary>
public sealed class SessionStoreConfig
{
    /// <summary>Store type. Supported values: InMemory, File, or Sqlite.</summary>
    public string? Type { get; set; }
    /// <summary>Path used by file-based session store implementation.</summary>
    public string? FilePath { get; set; }
    /// <summary>Connection string used by SQLite session store implementation.</summary>
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "session-store", Order = 2, Secret = true)]
    public string? ConnectionString { get; set; }
}

/// <summary>API key entry used for multi-tenant gateway auth.</summary>
public sealed class ApiKeyConfig
{
    /// <summary>The raw API key value.</summary>
    [Display(
        Name = "API key",
        Description = "The raw API key value used for gateway authentication. Sensitive: stored and shown masked.",
        GroupName = "API key",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "api-key", Order = 0, Secret = true)]
    public string? ApiKey { get; set; }
    /// <summary>Tenant identifier for multi-tenant isolation.</summary>
    public string? TenantId { get; set; }
    /// <summary>Caller identifier used in audit logs.</summary>
    public string? CallerId { get; set; }
    /// <summary>Human-readable name for this key.</summary>
    public string? DisplayName { get; set; }
    /// <summary>Agent IDs this key is allowed to access. Empty means all.</summary>
    public List<string>? AllowedAgents { get; set; }
    /// <summary>Permissions granted to this key (e.g. 'chat:send', 'sessions:read').</summary>
    public List<string>? Permissions { get; set; }
    /// <summary>Whether this key has administrative privileges.</summary>
    public bool IsAdmin { get; set; }
}

/// <summary>
/// Workspace and portal display settings.
/// Controls limits for report and file preview in the portal UI.
/// </summary>
public sealed class WorkspacePortalConfig
{
    /// <summary>
    /// Maximum number of bytes read from a report file for portal preview.
    /// Files larger than this are truncated server-side and flagged in the UI.
    /// Defaults to 524288 (512 KB). Set to 0 for no server-side limit.
    /// </summary>
    public int MaxReportFileSizeBytes { get; set; } = 512 * 1024;
}

/// <summary>
/// Auxiliary (cheap/fast) model configuration for background gateway tasks.
/// </summary>
public sealed class AuxiliaryConfig
{
    /// <summary>
    /// Conversation title generation settings. Hydrated by
    /// <c>AuxiliarySchemaContributor</c> as a nested object (<c>{ model, timeoutSeconds }</c>);
    /// this property must remain an object so the bound config matches the on-disk shape.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(TitlingConfigJsonConverter))]
    public TitlingConfig? Titling { get; set; }

    /// <summary>
    /// Model ID to use for session compaction summarisation (cheap/fast auxiliary model).
    /// Supports any registered provider model ID (e.g. "gpt-4o-mini", "claude-haiku-3-5").
    /// When null or empty the primary <see cref="CompactionOptions.SummarizationModel"/> or
    /// the compactor's default waterfall is used.
    /// If the resolved auxiliary model has a smaller context window than the compaction
    /// threshold, a startup warning is emitted but the gateway continues to run.
    /// </summary>
    public string? Compression { get; set; }
}

/// <summary>
/// Conversation auto-title generation settings under <c>gateway.auxiliary.titling</c>.
/// </summary>
public sealed class TitlingConfig
{
    /// <summary>
    /// Master switch for conversation auto-titling. When false the gateway never schedules a
    /// title-generation call and conversations keep their default title until a user or agent
    /// renames them. Defaults to true. Surfaced as config because the only prior way to disable
    /// auto-titling was to leave no models registered, which is a poor proxy for intent.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Model ID to use for auto-generating conversation titles after the first user+assistant
    /// exchange. Supports any registered provider model ID (e.g. "gpt-4o-mini",
    /// "claude-haiku-3-5", "gemini-2.0-flash-lite").
    /// When null or empty the primary session model is used as fallback.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Maximum time in seconds allowed for the best-effort title generation call before it is
    /// abandoned. Defaults to 30 seconds. A non-positive value falls back to the 30s default so a
    /// mis-set zero never produces a zero-timeout that cancels every call instantly.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}

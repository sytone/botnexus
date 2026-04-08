using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Platform-wide BotNexus configuration stored at ~/.botnexus/config.json.
/// </summary>
public sealed class PlatformConfig
{
    /// <summary>Optional JSON schema reference for editor IntelliSense/validation.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    /// <summary>Configuration schema version for forward compatibility.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gateway-specific settings.</summary>
    public GatewaySettingsConfig? Gateway { get; set; }

    /// <summary>Agent definitions keyed by agent ID.</summary>
    public Dictionary<string, AgentDefinitionConfig>? Agents { get; set; }

    /// <summary>Provider configurations keyed by provider name.</summary>
    public Dictionary<string, ProviderConfig>? Providers { get; set; }

    /// <summary>Channel settings keyed by channel name.</summary>
    public Dictionary<string, ChannelConfig>? Channels { get; set; }

    /// <summary>Extensions loading settings (legacy root-level form).</summary>
    public ExtensionsConfig? Extensions { get; set; }

    /// <summary>API key for Gateway authentication (null = dev mode, no auth).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Multi-tenant API keys keyed by key ID.</summary>
    public Dictionary<string, ApiKeyConfig>? ApiKeys { get; set; }

    /// <summary>Default Gateway listen URL (legacy root-level form).</summary>
    public string? ListenUrl { get; set; }

    /// <summary>Default agent to use when none specified (legacy root-level form).</summary>
    public string? DefaultAgentId { get; set; }

    /// <summary>Path to agents configuration directory (legacy root-level form).</summary>
    public string? AgentsDirectory { get; set; }

    /// <summary>Path to sessions storage directory (legacy root-level form).</summary>
    public string? SessionsDirectory { get; set; }

    /// <summary>Session store selection and configuration (legacy root-level form).</summary>
    public SessionStoreConfig? SessionStore { get; set; }

    /// <summary>CORS settings (legacy root-level form).</summary>
    public CorsConfig? Cors { get; set; }

    /// <summary>Rate limiting settings (legacy root-level form).</summary>
    public RateLimitConfig? RateLimit { get; set; }

    /// <summary>Cron scheduler settings and optional seed jobs.</summary>
    public CronConfig? Cron { get; set; }

    /// <summary>Logging level.</summary>
    public string? LogLevel { get; set; }

    /// <summary>Returns the configured listen URL, preferring the nested Gateway section.</summary>
    public string? GetListenUrl()
        => Gateway?.ListenUrl ?? ListenUrl;

    /// <summary>Returns the default agent ID, preferring the nested Gateway section.</summary>
    public string? GetDefaultAgentId()
        => Gateway?.DefaultAgentId ?? DefaultAgentId;

    /// <summary>Returns the agents directory, preferring the nested Gateway section.</summary>
    public string? GetAgentsDirectory()
        => Gateway?.AgentsDirectory ?? AgentsDirectory;

    /// <summary>Returns the sessions directory, preferring the nested Gateway section.</summary>
    public string? GetSessionsDirectory()
        => Gateway?.SessionsDirectory ?? SessionsDirectory;

    /// <summary>Returns the log level, preferring the nested Gateway section.</summary>
    public string? GetLogLevel()
        => Gateway?.LogLevel ?? LogLevel;

    /// <summary>Returns configured multi-tenant API keys, preferring the nested Gateway section.</summary>
    public Dictionary<string, ApiKeyConfig>? GetApiKeys()
        => Gateway?.ApiKeys ?? ApiKeys;

    /// <summary>Returns configured session store settings, preferring the nested Gateway section.</summary>
    public SessionStoreConfig? GetSessionStore()
        => Gateway?.SessionStore ?? SessionStore;

    /// <summary>Returns configured CORS settings, preferring the nested Gateway section.</summary>
    public CorsConfig? GetCors()
        => Gateway?.Cors ?? Cors;

    /// <summary>Returns configured extensions settings, preferring the nested Gateway section.</summary>
    public ExtensionsConfig? GetExtensions()
        => Gateway?.Extensions ?? Extensions;

    /// <summary>Returns configured rate limiting settings, preferring the nested Gateway section.</summary>
    public RateLimitConfig? GetRateLimit()
        => Gateway?.RateLimit ?? RateLimit;
}

/// <summary>Provider-specific configuration.</summary>
public sealed class ProviderConfig
{
    /// <summary>Whether this provider is enabled. Disabled providers are hidden from API.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>API key or reference to auth.json entry.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Base URL override.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Default model for this provider.</summary>
    public string? DefaultModel { get; set; }

    /// <summary>Allowed model IDs for this provider. Null means all models, empty means none.</summary>
    public List<string>? Models { get; set; }
}

/// <summary>Gateway runtime configuration.</summary>
public sealed class GatewaySettingsConfig
{
    /// <summary>Gateway HTTP listen URL.</summary>
    public string? ListenUrl { get; set; }
    /// <summary>Default agent to route to when none specified.</summary>
    public string? DefaultAgentId { get; set; }
    /// <summary>Path to agents configuration directory.</summary>
    public string? AgentsDirectory { get; set; }
    /// <summary>Path to sessions storage directory.</summary>
    public string? SessionsDirectory { get; set; }
    /// <summary>Session store selection and configuration.</summary>
    public SessionStoreConfig? SessionStore { get; set; }
    /// <summary>CORS settings for browser-based clients.</summary>
    public CorsConfig? Cors { get; set; }
    /// <summary>Per-client request rate limiting settings.</summary>
    public RateLimitConfig? RateLimit { get; set; }
    /// <summary>Logging level override.</summary>
    public string? LogLevel { get; set; }
    /// <summary>Multi-tenant API keys keyed by key ID.</summary>
    public Dictionary<string, ApiKeyConfig>? ApiKeys { get; set; }
    /// <summary>Extensions loading settings.</summary>
    public ExtensionsConfig? Extensions { get; set; }
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
    /// <summary>Maximum requests allowed in a window for a single client.</summary>
    public int RequestsPerMinute { get; set; } = 60;

    /// <summary>Window size in seconds used for request counting.</summary>
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>Cron scheduler configuration.</summary>
public sealed class CronConfig
{
    /// <summary>Whether the cron scheduler is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Scheduler polling interval in seconds.</summary>
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
}

/// <summary>Agent definition in platform config.</summary>
public sealed class AgentDefinitionConfig
{
    /// <summary>Provider name (e.g. 'copilot').</summary>
    public string? Provider { get; set; }
    /// <summary>Human-readable display name.</summary>
    public string? DisplayName { get; set; }
    /// <summary>Description of the agent's purpose.</summary>
    public string? Description { get; set; }
    /// <summary>Model identifier (e.g. 'gpt-4.1').</summary>
    public string? Model { get; set; }
    /// <summary>Model IDs this agent is allowed to use. Null means unrestricted within provider allowlist.</summary>
    public List<string>? AllowedModels { get; set; }
    /// <summary>Ordered list of files to load as the system prompt. Empty = default order.</summary>
    public List<string>? SystemPromptFiles { get; set; }
    /// <summary>Path to a single system prompt file (legacy, prefer SystemPromptFiles).</summary>
    public string? SystemPromptFile { get; set; }
    /// <summary>Tool identifiers this agent has access to.</summary>
    public List<string>? ToolIds { get; set; }
    /// <summary>Agent IDs this agent can call as sub-agents.</summary>
    public List<string>? SubAgents { get; set; }
    /// <summary>Isolation strategy name (e.g. 'in-process').</summary>
    public string? IsolationStrategy { get; set; }
    /// <summary>Maximum concurrent sessions for this agent.</summary>
    public int? MaxConcurrentSessions { get; set; }
    /// <summary>Agent-level metadata.</summary>
    public JsonElement? Metadata { get; set; }
    /// <summary>Strategy-specific isolation options.</summary>
    public JsonElement? IsolationOptions { get; set; }
    /// <summary>Whether this agent is enabled.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Memory system configuration for this agent.</summary>
    public MemoryAgentConfig? Memory { get; set; }
    /// <summary>Session access configuration for this agent's session tool.</summary>
    public SessionAccessConfig? SessionAccess { get; set; }
    /// <summary>Skills configuration for this agent.</summary>
    public BotNexus.Extensions.Skills.SkillsConfig? Skills { get; set; }
}

/// <summary>Controls what sessions an agent can access via the session tool.</summary>
public sealed class SessionAccessConfig
{
    /// <summary>Access level: "own" (default), "allowlist", or "all".</summary>
    public string Level { get; set; } = "own";
    /// <summary>Agent IDs this agent can view sessions for (when level is "allowlist").</summary>
    public List<string>? AllowedAgents { get; set; }
}

/// <summary>Channel definition in platform config.</summary>
public sealed class ChannelConfig
{
    /// <summary>Channel type (e.g. 'signalr', 'slack').</summary>
    public string? Type { get; set; }
    /// <summary>Whether this channel is enabled.</summary>
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
    public string? ConnectionString { get; set; }
}

/// <summary>API key entry used for multi-tenant gateway auth.</summary>
public sealed class ApiKeyConfig
{
    /// <summary>The raw API key value.</summary>
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

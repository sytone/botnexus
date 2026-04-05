namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Platform-wide BotNexus configuration stored at ~/.botnexus/config.json.
/// </summary>
public sealed class PlatformConfig
{
    /// <summary>Gateway-specific settings.</summary>
    public GatewaySettingsConfig? Gateway { get; set; }

    /// <summary>Agent definitions keyed by agent ID.</summary>
    public Dictionary<string, AgentDefinitionConfig>? Agents { get; set; }

    /// <summary>Provider configurations keyed by provider name.</summary>
    public Dictionary<string, ProviderConfig>? Providers { get; set; }

    /// <summary>Channel settings keyed by channel name.</summary>
    public Dictionary<string, ChannelConfig>? Channels { get; set; }

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
}

/// <summary>Provider-specific configuration.</summary>
public sealed class ProviderConfig
{
    /// <summary>API key or reference to auth.json entry.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Base URL override.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Default model for this provider.</summary>
    public string? DefaultModel { get; set; }
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
    /// <summary>Logging level override.</summary>
    public string? LogLevel { get; set; }
    /// <summary>Multi-tenant API keys keyed by key ID.</summary>
    public Dictionary<string, ApiKeyConfig>? ApiKeys { get; set; }
}

/// <summary>Agent definition in platform config.</summary>
public sealed class AgentDefinitionConfig
{
    /// <summary>Provider name (e.g. 'copilot').</summary>
    public string? Provider { get; set; }
    /// <summary>Model identifier (e.g. 'gpt-4.1').</summary>
    public string? Model { get; set; }
    /// <summary>Optional path to an external system prompt file.</summary>
    public string? SystemPromptFile { get; set; }
    /// <summary>Isolation strategy name (e.g. 'in-process').</summary>
    public string? IsolationStrategy { get; set; }
    /// <summary>Whether this agent is enabled.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Channel definition in platform config.</summary>
public sealed class ChannelConfig
{
    /// <summary>Channel type (e.g. 'websocket', 'slack').</summary>
    public string? Type { get; set; }
    /// <summary>Whether this channel is enabled.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Adapter-specific settings.</summary>
    public Dictionary<string, string>? Settings { get; set; }
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

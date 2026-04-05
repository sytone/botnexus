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

    public string? GetListenUrl()
        => Gateway?.ListenUrl ?? ListenUrl;

    public string? GetDefaultAgentId()
        => Gateway?.DefaultAgentId ?? DefaultAgentId;

    public string? GetAgentsDirectory()
        => Gateway?.AgentsDirectory ?? AgentsDirectory;

    public string? GetSessionsDirectory()
        => Gateway?.SessionsDirectory ?? SessionsDirectory;

    public string? GetLogLevel()
        => Gateway?.LogLevel ?? LogLevel;

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
    public string? ListenUrl { get; set; }
    public string? DefaultAgentId { get; set; }
    public string? AgentsDirectory { get; set; }
    public string? SessionsDirectory { get; set; }
    public string? LogLevel { get; set; }
    public Dictionary<string, ApiKeyConfig>? ApiKeys { get; set; }
}

/// <summary>Agent definition in platform config.</summary>
public sealed class AgentDefinitionConfig
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? SystemPromptFile { get; set; }
    public string? IsolationStrategy { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Channel definition in platform config.</summary>
public sealed class ChannelConfig
{
    public string? Type { get; set; }
    public bool Enabled { get; set; } = true;
    public Dictionary<string, string>? Settings { get; set; }
}

/// <summary>API key entry used for multi-tenant gateway auth.</summary>
public sealed class ApiKeyConfig
{
    public string? ApiKey { get; set; }
    public string? TenantId { get; set; }
    public string? CallerId { get; set; }
    public string? DisplayName { get; set; }
    public List<string>? AllowedAgents { get; set; }
    public List<string>? Permissions { get; set; }
    public bool IsAdmin { get; set; }
}

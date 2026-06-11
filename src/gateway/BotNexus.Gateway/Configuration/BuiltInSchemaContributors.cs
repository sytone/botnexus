using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Contributes default values for the <c>gateway</c> section of config.json.
/// Only includes settings that live directly under the gateway key (not nested sub-sections
/// which have their own contributors).
/// </summary>
public sealed class GatewaySchemaContributor : IConfigSchemaContributor
{
    public string SectionPath => "gateway";

    public object GetDefaults() => new
    {
        listenUrl = "http://localhost:5005",
        logLevel = "Information",
        enableProviderRequestLogging = false,
        shellPreference = "auto"
    };
}

/// <summary>
/// Contributes default values for the <c>gateway.compaction</c> section.
/// </summary>
public sealed class CompactionSchemaContributor : IConfigSchemaContributor
{
    public string SectionPath => "gateway.compaction";

    public object GetDefaults() => new CompactionOptions();
}

/// <summary>
/// Contributes default values for the <c>gateway.auxiliary</c> section.
/// </summary>
public sealed class AuxiliarySchemaContributor : IConfigSchemaContributor
{
    public string SectionPath => "gateway.auxiliary";

    public object GetDefaults() => new
    {
        titling = new
        {
            model = (string?)null,
            timeoutSeconds = 30
        }
    };
}

/// <summary>
/// Contributes default values for the <c>gateway.autoUpdate</c> section.
/// </summary>
public sealed class AutoUpdateSchemaContributor : IConfigSchemaContributor
{
    public string SectionPath => "gateway.autoUpdate";

    public object GetDefaults() => new AutoUpdateConfig();
}

/// <summary>
/// Contributes default values for the <c>cron</c> section.
/// </summary>
public sealed class CronSchemaContributor : IConfigSchemaContributor
{
    public string SectionPath => "cron";

    public object GetDefaults() => new
    {
        tickIntervalSeconds = 60
    };
}

/// <summary>
/// Contributes default values for the <c>gateway.sessionStore</c> section.
/// </summary>
public sealed class SessionStoreSchemaContributor : IConfigSchemaContributor
{
    public string SectionPath => "gateway.sessionStore";

    public object GetDefaults() => new
    {
        type = "Sqlite"
    };
}

/// <summary>
/// Contributes default values for the <c>gateway.rateLimit</c> section.
/// </summary>
public sealed class RateLimitSchemaContributor : IConfigSchemaContributor
{
    public string SectionPath => "gateway.rateLimit";

    public object GetDefaults() => new
    {
        enabled = false,
        requestsPerMinute = 300,
        windowSeconds = 60
    };
}

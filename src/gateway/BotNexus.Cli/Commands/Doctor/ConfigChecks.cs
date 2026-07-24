using System.Text.Json.Nodes;

namespace BotNexus.Cli.Commands.Doctor;

/// <summary>
/// Checks that <c>gateway.extensions</c> block exists and is enabled.
/// </summary>
public sealed class ExtensionsBlockCheck : IConfigCheck
{
    public string Id => "extensions-block";
    public string Description => "gateway.extensions block is absent or has extensions disabled.";
    public string FixDescription => "Add gateway.extensions = { enabled: true }";

    public bool IsApplicable(JsonObject root)
    {
        var gateway = root["gateway"] as JsonObject;
        if (gateway is null) return true;
        var extensions = gateway["extensions"] as JsonObject;
        if (extensions is null) return true;
        // present — only flag if explicitly disabled
        if (extensions["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var enabled))
            return !enabled;
        return false;
    }

    public void Apply(JsonObject root)
    {
        var gateway = root["gateway"] as JsonObject ?? new JsonObject();
        root["gateway"] = gateway;

        if (gateway["extensions"] is not JsonObject extensions)
        {
            extensions = new JsonObject();
            gateway["extensions"] = extensions;
        }
        extensions["enabled"] = true;
    }
}

/// <summary>
/// Checks that <c>gateway.extensions.defaults["botnexus-skills"]</c> is present and enabled.
/// </summary>
public sealed class SkillsWorldDefaultCheck : IConfigCheck
{
    public string Id => "skills-world-default";
    public string Description => "Skills extension has no world-level default in gateway.extensions.defaults.";
    public string FixDescription => "Add gateway.extensions.defaults[\"botnexus-skills\"].enabled = true";

    public bool IsApplicable(JsonObject root)
    {
        var defaults = GetDefaults(root);
        if (defaults is null) return true;
        var skills = defaults["botnexus-skills"] as JsonObject;
        if (skills is null) return true;
        if (skills["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var enabled))
            return !enabled;
        return false;
    }

    public void Apply(JsonObject root)
    {
        var gateway = root["gateway"] as JsonObject ?? new JsonObject();
        root["gateway"] = gateway;

        var extensions = gateway["extensions"] as JsonObject ?? new JsonObject();
        gateway["extensions"] = extensions;
        extensions["enabled"] = true;

        var defaults = extensions["defaults"] as JsonObject ?? new JsonObject();
        extensions["defaults"] = defaults;

        var skills = defaults["botnexus-skills"] as JsonObject ?? new JsonObject();
        defaults["botnexus-skills"] = skills;
        skills["enabled"] = true;
    }

    private static JsonObject? GetDefaults(JsonObject root)
        => (root["gateway"] as JsonObject)?["extensions"] as JsonObject is { } ext
            ? ext["defaults"] as JsonObject
            : null;
}

/// <summary>
/// Surfaces a keyless gateway that has explicitly <em>opted out</em> of the dev-mode browser-Origin
/// guard. As of #1946 the guard is ON by default: an absent flag means the <c>gateway-dev</c> admin
/// identity is already defended against DNS-rebind / CSRF from a malicious web origin. Only an
/// explicit <c>FeatureManagement.GatewayDevOriginEnforcement: false</c> leaves a keyless gateway
/// exposed, so that is the sole case this check recommends re-enabling.
/// <para>
/// Only applicable when NO API key is configured (keyless dev mode) and the
/// <c>FeatureManagement.GatewayDevOriginEnforcement</c> flag is explicitly set to <c>false</c>.
/// Applying the fix seeds <c>gateway.cors.allowedOrigins</c> with the localhost default (only if
/// unset, so an operator's existing origins are preserved) and turns the flag back on. Operators
/// who reach the UI over a non-localhost origin (LAN hostname, reverse proxy, netbird) must add
/// that origin to <c>gateway.cors.allowedOrigins</c> before re-enabling, or they will be locked out.
/// </para>
/// </summary>
public sealed class DevOriginEnforcementCheck : IConfigCheck
{
    /// <summary>Feature-flag name; must match ApiKeyGatewayAuthHandler.DevOriginEnforcementFeature.</summary>
    private const string FeatureName = "GatewayDevOriginEnforcement";
    private const string DefaultOrigin = "http://localhost:5005";

    public string Id => "devmode-origin-enforcement";
    public string Description =>
        "Gateway runs keyless (dev mode) with the browser-Origin guard explicitly disabled (FeatureManagement.GatewayDevOriginEnforcement: false) - the gateway-dev admin identity is reachable from any web origin (DNS-rebind/CSRF risk). The guard is ON by default; only an explicit opt-out leaves it exposed.";
    public string FixDescription =>
        "Re-enable FeatureManagement.GatewayDevOriginEnforcement (or remove the explicit false to restore the secure default) and seed gateway.cors.allowedOrigins = [\"http://localhost:5005\"]. WARNING: if you reach the UI over a non-localhost origin (LAN hostname / reverse proxy / netbird), add that origin to gateway.cors.allowedOrigins FIRST or you will be locked out on restart.";

    public bool IsApplicable(JsonObject root)
    {
        // Only relevant in keyless dev mode - a configured API key path is unaffected by this guard.
        if (HasAnyApiKey(root))
            return false;

        // #1946: the guard is ON by default, so an absent flag already protects the gateway.
        // Only an explicit opt-out (false) leaves the keyless gateway exposed and worth surfacing.
        return IsFeatureExplicitlyDisabled(root);
    }

    public void Apply(JsonObject root)
    {
        // Seed a localhost allow-list only if none exists, preserving any origins the operator set.
        var gateway = root["gateway"] as JsonObject ?? new JsonObject();
        root["gateway"] = gateway;
        var cors = gateway["cors"] as JsonObject ?? new JsonObject();
        gateway["cors"] = cors;
        if (cors["allowedOrigins"] is not JsonArray existing || existing.Count == 0)
        {
            cors["allowedOrigins"] = new JsonArray { DefaultOrigin };
        }

        // Turn the flag on under the FeatureManagement section (Microsoft.FeatureManagement schema).
        var featureManagement = root["FeatureManagement"] as JsonObject ?? new JsonObject();
        root["FeatureManagement"] = featureManagement;
        featureManagement[FeatureName] = true;
    }

    private static bool HasAnyApiKey(JsonObject root)
    {
        if (root["apiKey"] is JsonValue av && av.TryGetValue<string>(out var legacy) && !string.IsNullOrWhiteSpace(legacy))
            return true;

        var apiKeys = (root["gateway"] as JsonObject)?["apiKeys"] as JsonObject;
        return apiKeys is { Count: > 0 };
    }

    private static bool IsFeatureExplicitlyDisabled(JsonObject root)
    {
        var fm = root["FeatureManagement"] as JsonObject;
        if (fm is null)
            return false;

        // Microsoft.FeatureManagement accepts either a bool literal or an object with an
        // EnabledFor filter list; we only treat a literal `false` as an explicit opt-out. An
        // absent flag is the default-ON state (#1946) and is intentionally not surfaced here.
        return fm[FeatureName] is JsonValue v && v.TryGetValue<bool>(out var enabled) && !enabled;
    }
}

/// <summary>
/// Checks that the top-level <c>cron</c> block exists with scheduler enabled.
/// </summary>
public sealed class CronCheck : IConfigCheck
{
    public string Id => "cron-enabled";
    public string Description => "cron scheduler block is absent from config.";
    public string FixDescription => "Add cron = { enabled: true, tickIntervalSeconds: 60 }";

    public bool IsApplicable(JsonObject root)
        => root["cron"] is not JsonObject;

    public void Apply(JsonObject root)
    {
        if (root["cron"] is JsonObject) return;
        root["cron"] = new JsonObject
        {
            ["enabled"] = true,
            ["tickIntervalSeconds"] = 60
        };
    }
}

/// <summary>
/// Checks that <c>compaction.summarizationModel</c> is not set to an expensive reasoning model.
/// Reasoning models (claude-opus-4.6, o3, gpt-5) are overkill for summarization and may
/// return empty responses when the thinking parameter is misconfigured.
/// </summary>
public sealed class CompactionModelCheck : IConfigCheck
{
    public string Id => "compaction-model";
    public string Description => "compaction.summarizationModel uses an expensive reasoning model — may fail or waste tokens.";
    public string FixDescription => "Change compaction.summarizationModel to \"claude-haiku-4.5\" (fast, cheap, reliable for summarization)";

    private static readonly string[] ExpensiveModels =
    [
        "claude-opus-4.6", "claude-opus-4-6", "o3", "o4-mini",
        "gpt-5", "gpt-5.2", "claude-opus-4"
    ];

    public bool IsApplicable(JsonObject root)
    {
        var model = GetSummarizationModel(root);
        if (string.IsNullOrWhiteSpace(model)) return false; // no model set — different concern
        return ExpensiveModels.Any(e => model.Contains(e, StringComparison.OrdinalIgnoreCase));
    }

    public void Apply(JsonObject root)
    {
        var compaction = root["compaction"] as JsonObject ?? new JsonObject();
        root["compaction"] = compaction;
        compaction["summarizationModel"] = "claude-haiku-4.5";
    }

    private static string? GetSummarizationModel(JsonObject root)
        => (root["compaction"] as JsonObject)?["summarizationModel"]?.GetValue<string>();
}

/// <summary>
/// Checks that <c>compaction.summarizationModel</c> is configured at all.
/// Without an explicit model, the compactor falls back to a default waterfall
/// which may pick an expensive or unavailable model.
/// </summary>
public sealed class CompactionModelMissingCheck : IConfigCheck
{
    public string Id => "compaction-model-missing";
    public string Description => "compaction.summarizationModel is not configured — compactor will use default model waterfall.";
    public string FixDescription => "Set compaction.summarizationModel to \"claude-haiku-4.5\"";

    public bool IsApplicable(JsonObject root)
    {
        var compaction = root["compaction"] as JsonObject;
        if (compaction is null) return true;
        var model = compaction["summarizationModel"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(model);
    }

    public void Apply(JsonObject root)
    {
        var compaction = root["compaction"] as JsonObject ?? new JsonObject();
        root["compaction"] = compaction;
        compaction["summarizationModel"] = "claude-haiku-4.5";
    }
}

/// <summary>
/// Checks that <c>agents.defaults.memory</c> block is present.
/// </summary>
public sealed class MemoryAgentDefaultCheck : IConfigCheck
{
    public string Id => "memory-agent-default";
    public string Description => "agents.defaults.memory block is absent — memory indexing will not be enabled by default.";
    public string FixDescription => "Add agents.defaults.memory = { enabled: true, indexing: \"auto\" }";

    public bool IsApplicable(JsonObject root)
    {
        var agents = root["agents"] as JsonObject;
        if (agents is null) return false; // no agents block at all — not our concern
        var defaults = agents["defaults"] as JsonObject;
        return defaults?["memory"] is not JsonObject;
    }

    public void Apply(JsonObject root)
    {
        var agents = root["agents"] as JsonObject;
        if (agents is null) return;

        var defaults = agents["defaults"] as JsonObject ?? new JsonObject();
        agents["defaults"] = defaults;

        if (defaults["memory"] is JsonObject) return;
        defaults["memory"] = new JsonObject
        {
            ["enabled"] = true,
            ["indexing"] = "auto"
        };
    }
}

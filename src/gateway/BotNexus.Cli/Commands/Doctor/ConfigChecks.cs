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

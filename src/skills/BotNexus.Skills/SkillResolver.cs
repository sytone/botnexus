namespace BotNexus.Skills;

/// <summary>
/// Result of skill resolution.
/// </summary>
public sealed record SkillResolution
{
    public required IReadOnlyList<SkillDefinition> Loaded { get; init; }
    public required IReadOnlyList<SkillDefinition> Available { get; init; }
    public required IReadOnlyList<SkillDefinition> Denied { get; init; }
}

/// <summary>
/// Resolves which skills to load based on agent config and explicit load requests.
/// Skills are loaded when: autoLoad lists them, or they are explicitly loaded by the agent.
/// Skills are filtered by allow/deny lists from agent config.
/// </summary>
public static class SkillResolver
{
    public static SkillResolution Resolve(
        IReadOnlyList<SkillDefinition> allSkills,
        SkillsConfig? config,
        IReadOnlyList<string>? explicitlyLoaded = null)
    {
        config ??= new SkillsConfig();

        if (!config.Enabled)
            return new SkillResolution { Loaded = [], Available = [], Denied = [] };

        var denySet = config.Disabled is { Count: > 0 }
            ? new HashSet<string>(config.Disabled, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var allowSet = config.Allowed is { Count: > 0 }
            ? new HashSet<string>(config.Allowed, StringComparer.OrdinalIgnoreCase)
            : null;

        var denied = new List<SkillDefinition>();
        var eligible = new List<SkillDefinition>();

        foreach (var skill in allSkills)
        {
            if (denySet.Contains(skill.Name))
            {
                denied.Add(skill);
                continue;
            }

            if (allowSet is not null && !allowSet.Contains(skill.Name))
            {
                denied.Add(skill);
                continue;
            }

            eligible.Add(skill);
        }

        var autoLoadSet = config.AutoLoad is { Count: > 0 }
            ? new HashSet<string>(config.AutoLoad, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var explicitSet = explicitlyLoaded is { Count: > 0 }
            ? new HashSet<string>(explicitlyLoaded, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var loaded = new List<SkillDefinition>();
        var available = new List<SkillDefinition>();
        var totalChars = 0;

        foreach (var skill in eligible)
        {
            var shouldLoad = autoLoadSet.Contains(skill.Name) || explicitSet.Contains(skill.Name);

            if (!shouldLoad)
            {
                available.Add(skill);
                continue;
            }

            if (loaded.Count >= config.MaxLoadedSkills)
            {
                available.Add(skill);
                continue;
            }

            if (totalChars + skill.Content.Length > config.MaxSkillContentChars)
            {
                available.Add(skill);
                continue;
            }

            loaded.Add(skill);
            totalChars += skill.Content.Length;
        }

        return new SkillResolution { Loaded = loaded, Available = available, Denied = denied };
    }
}

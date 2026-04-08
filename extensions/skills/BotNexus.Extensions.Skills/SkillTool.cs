using System.Text.Json;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Providers.Core.Models;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Agent-facing tool for listing and loading skills at runtime.
/// Re-discovers skills on each call to pick up filesystem changes.
/// </summary>
public sealed class SkillTool(
    string? globalSkillsDir,
    string? agentSkillsDir,
    string? workspaceSkillsDir,
    SkillsConfig? config) : IAgentTool
{
    private readonly HashSet<string> _sessionLoaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a SkillTool with a static skill list (for testing).</summary>
    internal SkillTool(IReadOnlyList<SkillDefinition> allSkills, SkillsConfig? config)
        : this(null, null, null, config)
    {
        _staticSkills = allSkills;
    }

    private readonly IReadOnlyList<SkillDefinition>? _staticSkills;

    private IReadOnlyList<SkillDefinition> DiscoverSkills()
        => _staticSkills ?? SkillDiscovery.Discover(globalSkillsDir, agentSkillsDir, workspaceSkillsDir);

    public string Name => "skills";
    public string Label => "Skill Manager";

    public Tool Definition => new(
        Name,
        "List available skills and load them into context. Use when you need domain-specific knowledge.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["list", "load"],
                  "description": "Action: 'list' shows available skills and their descriptions, 'load' activates a skill."
                },
                "skillName": {
                  "type": "string",
                  "description": "Skill name to load (required for 'load' action)."
                }
              },
              "required": ["action"]
            }
            """).RootElement.Clone());

    /// <summary>Gets the set of skill names explicitly loaded during this session.</summary>
    public IReadOnlySet<string> SessionLoadedSkills => _sessionLoaded;

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(arguments);
    }

    public Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var action = ReadString(arguments, "action") ?? "list";
        return Task.FromResult(action.ToLowerInvariant() switch
        {
            "list" => ListSkills(),
            "load" => LoadSkill(arguments),
            _ => TextResult($"Unknown action: {action}")
        });
    }

    private AgentToolResult ListSkills()
    {
        var currentSkills = DiscoverSkills();
        var resolution = SkillResolver.Resolve(currentSkills, config, explicitlyLoaded: _sessionLoaded.ToList());

        var lines = new List<string>();
        if (resolution.Loaded.Count > 0)
        {
            lines.Add("## Loaded Skills");
            foreach (var s in resolution.Loaded)
                lines.Add($"- **{s.Name}**: {s.Description}");
            lines.Add("");
        }

        if (resolution.Available.Count > 0)
        {
            lines.Add("## Available Skills (not loaded)");
            lines.Add("Use `skills` tool with action `load` and the skill name to activate.");
            foreach (var s in resolution.Available)
                lines.Add($"- **{s.Name}**: {s.Description}");
            lines.Add("");
        }

        if (lines.Count == 0)
            lines.Add("No skills available.");

        return TextResult(string.Join("\n", lines));
    }

    private AgentToolResult LoadSkill(IReadOnlyDictionary<string, object?> arguments)
    {
        var skillName = ReadString(arguments, "skillName");
        if (string.IsNullOrWhiteSpace(skillName))
            return TextResult("Error: skillName is required for load action.");

        if (config is not null && !config.Enabled)
            return TextResult("Skills are disabled for this agent.");

        var currentSkills = DiscoverSkills();
        var skill = currentSkills.FirstOrDefault(s => string.Equals(s.Name, skillName, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
            return TextResult($"Skill '{skillName}' not found. Use action 'list' to see available skills.");

        if (_sessionLoaded.Contains(skill.Name))
            return TextResult($"Skill '{skill.Name}' is already loaded.");

        // Delegate access checks to the resolver — it handles deny, allow, and limits
        var resolution = SkillResolver.Resolve(currentSkills, config, explicitlyLoaded: [skill.Name]);
        if (resolution.Denied.Any(s => string.Equals(s.Name, skillName, StringComparison.OrdinalIgnoreCase)))
            return TextResult($"Skill '{skillName}' is not available for this agent.");

        if (!resolution.Loaded.Any(s => string.Equals(s.Name, skillName, StringComparison.OrdinalIgnoreCase)))
            return TextResult($"Skill '{skillName}' cannot be loaded (budget exceeded).");

        _sessionLoaded.Add(skill.Name);

        return TextResult($"""
            ## Skill: {skill.Name}
            
            {skill.Content}
            """);
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            JsonElement el => el.ToString(),
            _ => value.ToString()
        };
    }

    private static AgentToolResult TextResult(string text)
        => new([new AgentToolContent(AgentToolContentType.Text, text)]);
}

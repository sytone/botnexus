using System.Collections.Concurrent;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Extensions.Skills.Security;
using System.IO.Abstractions;

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
    private readonly IFileSystem _fileSystem = new FileSystem();
    private readonly ConcurrentDictionary<string, byte> _sessionLoaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a SkillTool with a static skill list (for testing).</summary>
    internal SkillTool(IReadOnlyList<SkillDefinition> allSkills, SkillsConfig? config)
        : this(null, null, null, config)
    {
        _staticSkills = allSkills;
    }

    /// <summary>Creates a SkillTool with a static skill list and an injected filesystem (for testing view_file).</summary>
    internal SkillTool(IReadOnlyList<SkillDefinition> allSkills, SkillsConfig? config, IFileSystem fileSystem)
        : this(null, null, null, config)
    {
        _staticSkills = allSkills;
        _fileSystem = fileSystem;
    }

    // Support sub-directories whose files are exposed as load-on-demand linked context.
    private static readonly HashSet<string> AllowedLinkedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "references", "templates", "scripts", "assets"
    };

    private const int MaxLinkedFileBytes = 262_144; // 256 KB - keep single-file loads context-safe

    private readonly IReadOnlyList<SkillDefinition>? _staticSkills;

    private IReadOnlyList<SkillDefinition> DiscoverSkills()
        => _staticSkills ?? SkillDiscovery.Discover(globalSkillsDir, agentSkillsDir, workspaceSkillsDir, _fileSystem);

    public IReadOnlyList<SkillDefinition> GetDiscoveredSkills() => DiscoverSkills();

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
                  "enum": ["list", "load", "view_file"],
                  "description": "Action: 'list' shows available skills and their descriptions, 'load' activates a skill and lists its linked support files, 'view_file' loads a single linked support file without injecting the whole skill."
                },
                "skillName": {
                  "type": "string",
                  "description": "Skill name to load or view a file from (required for 'load' and 'view_file' actions)."
                },
                "filePath": {
                  "type": "string",
                  "description": "Relative path (within the skill directory) of the linked support file to view (required for 'view_file'). Must live under references/, templates/, scripts/, or assets/."
                }
              },
              "required": ["action"]
            }
            """).RootElement.Clone());

    /// <summary>Gets the set of skill names explicitly loaded during this session.</summary>
    public IReadOnlySet<string> SessionLoadedSkills => _sessionLoaded.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    public SkillsConfig? Config => config;
    public (string? Global, string? Agent, string? Workspace) DiscoveryPaths
        => (globalSkillsDir, agentSkillsDir, workspaceSkillsDir);

    public bool TryUnload(string skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
            return false;

        return _sessionLoaded.TryRemove(skillName, out _);
    }

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
            "view_file" => ViewFile(arguments),
            _ => TextResult($"Unknown action: {action}")
        });
    }

    private AgentToolResult ListSkills()
    {
        var currentSkills = DiscoverSkills();
        var resolution = SkillResolver.Resolve(currentSkills, config, explicitlyLoaded: _sessionLoaded.Keys.ToList());

        var lines = new List<string>();
        if (resolution.Loaded.Count > 0)
        {
            lines.Add("## Loaded Skills");
            foreach (var s in resolution.Loaded)
            {
                lines.Add($"- **{s.Name}**: {s.Description}");
                lines.Add($"  Path: {s.SourcePath}");
            }
            lines.Add("");
        }

        if (resolution.Available.Count > 0)
        {
            lines.Add("## Available Skills (not loaded)");
            lines.Add("Use `skills` tool with action `load` and the skill name to activate.");
            foreach (var s in resolution.Available)
            {
                lines.Add($"- **{s.Name}**: {s.Description}");
                if (!string.IsNullOrEmpty(s.SourcePath))
                    lines.Add($"  Path: {s.SourcePath}");
            }
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

        if (_sessionLoaded.ContainsKey(skill.Name))
            return TextResult($"Skill '{skill.Name}' is already loaded.");

        // Delegate access checks to the resolver - it handles deny, allow, and limits
        var resolution = SkillResolver.Resolve(currentSkills, config, explicitlyLoaded: [skill.Name]);
        if (resolution.Denied.Any(s => string.Equals(s.Name, skillName, StringComparison.OrdinalIgnoreCase)))
            return TextResult($"Skill '{skillName}' is not available for this agent.");

        if (!resolution.Loaded.Any(s => string.Equals(s.Name, skillName, StringComparison.OrdinalIgnoreCase)))
            return TextResult($"Skill '{skillName}' cannot be loaded (budget exceeded).");

        if (!_sessionLoaded.TryAdd(skill.Name, 0))
            return TextResult($"Skill '{skill.Name}' is already loaded.");

        return TextResult($"""
            ## Skill: {skill.Name}
            **Path:** {skill.SourcePath}

            {skill.Content}
            {RenderLinkedFiles(skill)}
            """);
    }

    /// <summary>
    /// Renders the "Linked files" listing grouped by support directory plus a usage hint
    /// pointing at the <c>view_file</c> action. Returns an empty string when the skill has
    /// no bundled support files so plain skills render unchanged.
    /// </summary>
    private static string RenderLinkedFiles(SkillDefinition skill)
    {
        if (skill.LinkedFiles.Count == 0)
            return string.Empty;

        var lines = new List<string> { string.Empty, "### Linked files" };

        foreach (var group in skill.LinkedFiles
                     .GroupBy(f => f.Directory, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            lines.Add($"- **{group.Key}/**");
            foreach (var file in group.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
                lines.Add($"  - `{file.RelativePath}` ({file.SizeBytes} bytes)");
        }

        lines.Add(string.Empty);
        lines.Add($"Use the `skills` tool with action `view_file`, `skillName: {skill.Name}`, and the linked `filePath` to load an individual support file on demand (the whole skill directory is not injected).");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Loads a single linked support file from a skill without injecting the entire skill
    /// directory. The requested path is validated to prevent traversal, must resolve inside
    /// the skill directory, and honours the existing symlink/trust boundary checks.
    /// </summary>
    private AgentToolResult ViewFile(IReadOnlyDictionary<string, object?> arguments)
    {
        var skillName = ReadString(arguments, "skillName");
        if (string.IsNullOrWhiteSpace(skillName))
            return TextResult("Error: skillName is required for view_file action.");

        var relPath = ReadString(arguments, "filePath");
        if (string.IsNullOrWhiteSpace(relPath))
            return TextResult("Error: filePath is required for view_file action.");

        if (config is not null && !config.Enabled)
            return TextResult("Skills are disabled for this agent.");

        var currentSkills = DiscoverSkills();
        var skill = currentSkills.FirstOrDefault(s => string.Equals(s.Name, skillName, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
            return TextResult($"Skill '{skillName}' not found. Use action 'list' to see available skills.");

        // Access checks: a denied skill's files must not be readable either.
        var resolution = SkillResolver.Resolve(currentSkills, config, explicitlyLoaded: [skill.Name]);
        if (resolution.Denied.Any(s => string.Equals(s.Name, skillName, StringComparison.OrdinalIgnoreCase)))
            return TextResult($"Skill '{skillName}' is not available for this agent.");

        if (!IsAllowedLinkedPath(relPath, out var reason))
            return TextResult($"Error: {reason}");

        var targetPath = Path.Combine(skill.SourcePath, relPath);

        // Reuse the shared symlink/boundary validator: resolved path must stay inside the skill dir.
        if (!SkillPathValidator.TryValidate(targetPath, skill.SourcePath, _fileSystem, out targetPath, out var symlinkError))
            return TextResult($"Error: {symlinkError}");

        if (!_fileSystem.File.Exists(targetPath))
            return TextResult($"Linked file '{relPath}' not found in skill '{skill.Name}'.");

        var info = _fileSystem.FileInfo.New(targetPath);
        if (info.Length > MaxLinkedFileBytes)
            return TextResult($"Linked file '{relPath}' is too large to load ({info.Length} bytes; limit {MaxLinkedFileBytes}).");

        var content = _fileSystem.File.ReadAllText(targetPath);
        var normalized = relPath.Replace('\\', '/');

        return TextResult($"""
            ## Skill file: {skill.Name}/{normalized}
            **Path:** {targetPath}

            {content}
            """);
    }

    /// <summary>
    /// Validates a relative linked-file path: no absolute paths, no parent-directory traversal,
    /// and the file must live under one of the allowed support directories.
    /// </summary>
    private static bool IsAllowedLinkedPath(string relPath, out string reason)
    {
        reason = string.Empty;

        var normalized = relPath.Replace('\\', '/').TrimStart('/');

        if (Path.IsPathRooted(normalized) || normalized.Contains("../") || normalized.Contains("..\\") || normalized == ".." )
        {
            reason = "Path traversal is not permitted in filePath.";
            return false;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !AllowedLinkedDirs.Contains(parts[0]))
        {
            reason = $"Linked files must be under one of: {string.Join(", ", AllowedLinkedDirs)}/. Got: '{relPath}'.";
            return false;
        }

        return true;
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

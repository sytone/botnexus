using System.Collections.Concurrent;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Extensions.Skills.Recording;
using BotNexus.Extensions.Skills.Security;
using BotNexus.Extensions.Skills.Telemetry;
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
    SkillsConfig? config,
    ISkillUsageTelemetry? telemetry = null,
    string? pluginRootDir = null) : IAgentTool
{
    private readonly IFileSystem _fileSystem = new FileSystem();
    private readonly ConcurrentDictionary<string, byte> _sessionLoaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a SkillTool with a static skill list (for testing).</summary>
    internal SkillTool(IReadOnlyList<SkillDefinition> allSkills, SkillsConfig? config, ISkillUsageTelemetry? telemetry = null)
        : this(null, null, null, config, telemetry)
    {
        _staticSkills = allSkills;
    }

    /// <summary>Creates a SkillTool with a static skill list and an injected filesystem (for testing view_file).</summary>
    internal SkillTool(IReadOnlyList<SkillDefinition> allSkills, SkillsConfig? config, IFileSystem fileSystem, ISkillUsageTelemetry? telemetry = null)
        : this(null, null, null, config, telemetry)
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
        => _staticSkills ?? SkillDiscovery.Discover(
            globalSkillsDir,
            agentSkillsDir,
            workspaceSkillsDir,
            _fileSystem,
            // Plugin skills join at the global/shared tier (#2684). Resolved on every call for the
            // same reason the directories are re-scanned: a plugin installed mid-session must
            // become visible without a restart.
            pluginSkillsDirs: PluginSkillRootResolver.Resolve(pluginRootDir, _fileSystem),
            // #3355: scoped operator acknowledgements let a skill that legitimately shells out load.
            securityAcknowledgements: config?.SecurityAcknowledgements);

    public IReadOnlyList<SkillDefinition> GetDiscoveredSkills() => DiscoverSkills();

    public string Name => "skills";
    public string Label => "Skill Manager";

    /// <summary>Content source classification for turn-taint accumulation (#2519). Skill content is authored and reviewed in-repo/on-disk by the operator.</summary>
    public string ContentSource => ToolContentSource.Local;

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
                },
                "parameters": {
                  "type": "object",
                  "description": "Values for a parameterised skill, as a flat object of slotName -> value. Only for 'load', and only for skills whose listing shows parameters. Every declared parameter must be supplied; loading with one missing is refused rather than substituted blank.",
                  "additionalProperties": { "type": "string" }
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

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var action = ReadString(arguments, "action") ?? "list";
        return action.ToLowerInvariant() switch
        {
            "list" => await ListSkillsAsync(cancellationToken).ConfigureAwait(false),
            "load" => await LoadSkillAsync(arguments, cancellationToken).ConfigureAwait(false),
            "view_file" => await ViewFileAsync(arguments, cancellationToken).ConfigureAwait(false),
            _ => TextResult($"Unknown action: {action}")
        };
    }

    private async Task<AgentToolResult> ListSkillsAsync(CancellationToken cancellationToken)
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
                AppendParameterHint(lines, s);
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
                AppendParameterHint(lines, s);
            }
            lines.Add("");
        }

        if (lines.Count == 0)
            lines.Add("No skills available.");

        // Record a view for every surfaced skill so telemetry reflects listing activity (#1833).
        foreach (var s in resolution.Loaded.Concat(resolution.Available))
            await RecordAsync(t => t.RecordViewAsync(s.Name, cancellationToken)).ConfigureAwait(false);

        return TextResult(string.Join("\n", lines));
    }

    private async Task<AgentToolResult> LoadSkillAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
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

        // Parameter substitution runs BEFORE the skill is marked loaded: a refused load must leave
        // the session exactly as it was, or a retry with the corrected values reports "already
        // loaded" and the caller never gets the content.
        if (!TryResolveParameters(skill, arguments, out var parameterValues, out var parameterError))
            return TextResult(parameterError);

        if (!_sessionLoaded.TryAdd(skill.Name, 0))
            return TextResult($"Skill '{skill.Name}' is already loaded.");

        // Record the load as a use once it has actually been added to the session (#1833).
        await RecordAsync(t => t.RecordUseAsync(skill.Name, cancellationToken)).ConfigureAwait(false);

        var body = parameterValues.Count > 0
            ? SkillDraftValidator.Substitute(skill.Content, parameterValues)
            : skill.Content;

        return TextResult($"""
            ## Skill: {skill.Name}
            **Path:** {skill.SourcePath}
            {RenderAppliedParameters(parameterValues)}
            **Resolved from:** {DescribeRoot(skill.Source)} skill root

            Resolve scripts and support files against this directory - skills live under more than
            one root and the shared root is not always the right one (#3712).

            {body}
            {RenderLinkedFiles(skill)}
            """);
    }

    /// <summary>
    /// Matches the values supplied on a load against the parameters the skill declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three refusals, and each exists because the silent alternative produces a skill that looks
    /// like it worked. A MISSING value would leave <c>{{slot}}</c> in the instructions, so a replay
    /// runs the placeholder as if it were the value. An UNKNOWN value is a caller believing it
    /// changed something it did not — usually a renamed slot. And supplying values to a skill that
    /// declares NONE means the caller has the wrong skill, or a skill that lost its declarations in
    /// an edit; either way, substituting nothing and saying nothing is the wrong answer.
    /// </para>
    /// <para>
    /// A skill that declares no parameters and is loaded without any takes this path to a byte-identical
    /// result — which is every skill that existed before recording did.
    /// </para>
    /// </remarks>
    private static bool TryResolveParameters(
        SkillDefinition skill,
        IReadOnlyDictionary<string, object?> arguments,
        out IReadOnlyDictionary<string, string> values,
        out string error)
    {
        values = EmptyParameters;
        error = string.Empty;

        var supplied = ReadParameterMap(arguments);

        // A case-insensitive VIEW of the declarations, rather than trusting the comparer the
        // definition happens to carry. SkillParser always produces one, but a SkillDefinition built
        // in code need not, and a declaration matched by one comparer while substitution uses
        // another yields a load that reports success with the placeholder still in the text.
        var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in skill.Parameters)
            declared[pair.Key] = pair.Value;

        if (declared.Count == 0)
        {
            if (supplied.Count == 0)
                return true;

            error = $"Skill '{skill.Name}' declares no parameters, but {supplied.Count} " +
                    $"({string.Join(", ", supplied.Keys.Order(StringComparer.Ordinal))}) were supplied. " +
                    "Load it without parameters, or check whether you meant a different skill.";
            return false;
        }

        var unknown = supplied.Keys
            .Where(k => !declared.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        if (unknown.Count > 0)
        {
            error = $"Skill '{skill.Name}' does not declare: {string.Join(", ", unknown)}. " +
                    $"It declares: {string.Join(", ", declared.Keys.Order(StringComparer.Ordinal))}.";
            return false;
        }

        var missing = declared
            .Where(p => !supplied.ContainsKey(p.Key))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToList();

        if (missing.Count > 0)
        {
            var described = missing.Select(p => string.IsNullOrWhiteSpace(p.Value)
                ? $"- {p.Key}"
                : $"- {p.Key}: {p.Value}");

            error = $"Skill '{skill.Name}' requires parameter(s) that were not supplied. Loading " +
                    "without them would leave the placeholders in the instructions, so the load was " +
                    $"refused.\n{string.Join("\n", described)}";
            return false;
        }

        values = supplied;
        return true;
    }

    /// <summary>Reads the flat <c>parameters</c> object off a load call. Absent reads as empty.</summary>
    private static IReadOnlyDictionary<string, string> ReadParameterMap(
        IReadOnlyDictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("parameters", out var raw) || raw is null)
            return EmptyParameters;

        JsonElement element;
        if (raw is JsonElement je)
        {
            element = je;
        }
        else
        {
            try
            {
                element = JsonSerializer.SerializeToElement(raw);
            }
            catch (NotSupportedException)
            {
                return EmptyParameters;
            }
        }

        if (element.ValueKind != JsonValueKind.Object)
            return EmptyParameters;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            map[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.ToString();
        }

        return map;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyParameters =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names a skill's required parameters in the listing.
    /// </summary>
    /// <remarks>
    /// Without this the parameters are discoverable only by loading the skill and being refused.
    /// A required argument that is announced by an error message is an argument nobody supplies
    /// first time, and the retry costs a whole turn.
    /// </remarks>
    private static void AppendParameterHint(List<string> lines, SkillDefinition skill)
    {
        if (skill.Parameters.Count == 0)
            return;

        var names = skill.Parameters.Keys.Order(StringComparer.Ordinal);
        lines.Add($"  Requires parameters: {string.Join(", ", names)}");
    }

    /// <summary>
    /// Echoes the values that were substituted, so the filled-in skill is self-describing: the
    /// reader can tell which words in the instructions came from the caller and which were written
    /// into the skill.
    /// </summary>
    private static string RenderAppliedParameters(IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0)
            return string.Empty;

        var lines = values
            .OrderBy(v => v.Key, StringComparer.Ordinal)
            .Select(v => $"- `{v.Key}` = `{v.Value}`");

        return $"\n**Parameters applied:**\n{string.Join("\n", lines)}\n";
    }

    /// <summary>
    /// Names the discovery tier a skill was resolved from (#3712). The bare path alone is not
    /// enough: with four roots in play, an agent that sees only a directory cannot tell whether
    /// the skill is shared or agent-local, and hard-codes the shared root for a local skill -
    /// producing a "not recognized as the name of a script file" error that names the wrong
    /// problem. Naming the tier makes the correct root visible at point of use.
    /// </summary>
    private static string DescribeRoot(SkillSource source)
        => source switch
        {
            SkillSource.Plugin => "Plugin",
            SkillSource.Global => "Global",
            SkillSource.Agent => "Agent",
            SkillSource.Workspace => "Workspace",
            _ => source.ToString()
        };

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
    private async Task<AgentToolResult> ViewFileAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
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

        var candidatePath = Path.Combine(skill.SourcePath, relPath);

        // Reuse the shared symlink/boundary validator: resolved path must stay inside the skill dir.
        if (!SkillPathValidator.TryValidate(candidatePath, SkillPath.CreateRoot(skill.SourcePath, _fileSystem), _fileSystem, out var validated, out var symlinkError))
            return TextResult($"Error: {symlinkError}");

        var targetPath = validated.Value;

        if (!_fileSystem.File.Exists(targetPath))
            return TextResult($"Linked file '{relPath}' not found in skill '{skill.Name}'.");

        var info = _fileSystem.FileInfo.New(targetPath);
        if (info.Length > MaxLinkedFileBytes)
            return TextResult($"Linked file '{relPath}' is too large to load ({info.Length} bytes; limit {MaxLinkedFileBytes}).");

        var content = _fileSystem.File.ReadAllText(targetPath);
        var normalized = relPath.Replace('\\', '/');

        // Viewing a support file counts as a view of the owning skill (#1833).
        await RecordAsync(t => t.RecordViewAsync(skill.Name, cancellationToken)).ConfigureAwait(false);

        return TextResult($"""
            ## Skill file: {skill.Name}/{normalized}
            **Path:** {targetPath}

            {content}
            """);
    }

    /// <summary>
    /// Runs a best-effort telemetry recording action. Telemetry is an observability side-channel:
    /// a failure to record must never break skill listing/loading, so any exception (including a
    /// transient SQLite lock) is swallowed. No-ops when no telemetry sink is configured.
    /// </summary>
    private async Task RecordAsync(Func<ISkillUsageTelemetry, Task> record)
    {
        if (telemetry is null)
            return;

        try
        {
            await record(telemetry).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: telemetry must not affect the skill tool's result.
        }
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

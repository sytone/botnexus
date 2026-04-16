using System.Text;
using BotNexus.Gateway.Abstractions.Extensions;
using System.IO.Abstractions;

namespace BotNexus.Extensions.Skills;

public sealed class SkillsCommandContributor : ICommandContributor
{
    private static readonly IReadOnlyList<CommandDescriptor> Commands =
    [
        new CommandDescriptor
        {
            Name = "/skills",
            Description = "Manage discovered skills for the active session.",
            Category = "Skills",
            SubCommands =
            [
                new SubCommandDescriptor { Name = "list", Description = "Show discovered skills by status." },
                new SubCommandDescriptor
                {
                    Name = "info",
                    Description = "Show metadata for a skill.",
                    Arguments = [new CommandArgumentDescriptor { Name = "name", Description = "Skill name.", Required = true }]
                },
                new SubCommandDescriptor
                {
                    Name = "add",
                    Description = "Load a skill into this session.",
                    Arguments = [new CommandArgumentDescriptor { Name = "name", Description = "Skill name.", Required = true }]
                },
                new SubCommandDescriptor
                {
                    Name = "remove",
                    Description = "Unload a skill from this session.",
                    Arguments = [new CommandArgumentDescriptor { Name = "name", Description = "Skill name.", Required = true }]
                },
                new SubCommandDescriptor { Name = "reload", Description = "Re-discover skills from disk." }
            ]
        }
    ];

    private readonly Lock _snapshotLock = new();
    private readonly IFileSystem _fileSystem = new FileSystem();
    private Dictionary<string, SkillSnapshot>? _lastDiscovery;

    public IReadOnlyList<CommandDescriptor> GetCommands() => Commands;

    public async Task<CommandResult> ExecuteAsync(
        string commandName,
        CommandExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(commandName, "/skills", StringComparison.OrdinalIgnoreCase))
        {
            return Error("Command Not Found", $"Unknown skills command: {commandName}");
        }

        if (!TryResolveTool(context, out var skillTool, out var errorResult))
            return errorResult!;

        var resolvedTool = skillTool;
        return (context.SubCommand ?? "list").ToLowerInvariant() switch
        {
            "list" => BuildListResult(resolvedTool, context.AgentId),
            "info" => BuildInfoResult(resolvedTool, context.Arguments),
            "add" => await AddSkillAsync(resolvedTool, context.Arguments, cancellationToken).ConfigureAwait(false),
            "remove" => RemoveSkill(resolvedTool, context.Arguments),
            "reload" => ReloadSkills(resolvedTool),
            _ => Error("Invalid Sub-command", "Usage: /skills [list|info <name>|add <name>|remove <name>|reload]")
        };
    }

    private static bool TryResolveTool(
        CommandExecutionContext context,
        out SkillTool skillTool,
        out CommandResult? errorResult)
    {
        skillTool = null!;
        errorResult = null;

        if (context.ResolveSessionTool is null)
        {
            errorResult = Error("Skills Unavailable", "No active session tool resolver. Open an agent session and try again.");
            return false;
        }

        var resolvedTool = context.ResolveSessionTool("skills") as SkillTool;
        if (resolvedTool is null)
        {
            errorResult = Error("Skills Unavailable", "The skills tool is not available for this session.");
            return false;
        }

        skillTool = resolvedTool;
        return true;
    }

    private CommandResult BuildListResult(SkillTool skillTool, string? agentId)
    {
        var state = BuildState(skillTool);
        var config = skillTool.Config ?? new SkillsConfig();

        var maxLoaded = config.MaxLoadedSkills < 0 ? int.MaxValue : config.MaxLoadedSkills;
        var maxChars = config.MaxSkillContentChars < 0 ? int.MaxValue : config.MaxSkillContentChars;
        var usedTokens = state.Resolution.Loaded.Sum(skill => EstimateTokens(skill.Content.Length));
        var budgetTokens = maxChars == int.MaxValue ? int.MaxValue : EstimateTokens(maxChars);

        var body = new StringBuilder();
        body.AppendLine($"Skills for {agentId ?? "current agent"}");
        AppendSkillGroup(body, "Loaded", state.Resolution.Loaded);
        AppendSkillGroup(body, "Available", state.Resolution.Available);
        AppendDeniedGroup(body, state.Resolution.Denied);
        body.AppendLine($"  Config: max {(maxLoaded == int.MaxValue ? "unlimited" : maxLoaded.ToString())} loaded, ~{FormatThousands(budgetTokens)} token budget, ~{FormatThousands(usedTokens)} used");

        return new CommandResult
        {
            Title = $"Skills ({state.AllSkills.Count})",
            Body = body.ToString().TrimEnd(),
            IsError = false
        };
    }

    private CommandResult BuildInfoResult(SkillTool skillTool, IReadOnlyList<string> args)
    {
        var skillName = args.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(skillName))
            return Error("Missing Argument", "Usage: /skills info <name>");

        var state = BuildState(skillTool);
        var skill = state.AllSkills.FirstOrDefault(s => string.Equals(s.Name, skillName, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
            return Error("Skill Not Found", $"Skill '{skillName}' was not found.");

        var loadedSet = new HashSet<string>(state.Resolution.Loaded.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var deniedSet = new HashSet<string>(state.Resolution.Denied.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var explicitSet = new HashSet<string>(skillTool.SessionLoadedSkills, StringComparer.OrdinalIgnoreCase);
        var autoLoadSet = new HashSet<string>(skillTool.Config?.AutoLoad ?? [], StringComparer.OrdinalIgnoreCase);

        var status = deniedSet.Contains(skill.Name)
            ? "Denied"
            : loadedSet.Contains(skill.Name)
                ? autoLoadSet.Contains(skill.Name) && !explicitSet.Contains(skill.Name)
                    ? "Loaded (auto-load)"
                    : "Loaded"
                : "Available";

        var files = DescribeSkillFiles(skill.SourcePath);
        var allowedTools = string.IsNullOrWhiteSpace(skill.AllowedTools) ? "--" : skill.AllowedTools.Replace(",", ", ");

        var body = $"""
                    Skill: {skill.Name}
                      Name:          {skill.Name}
                      Description:   {skill.Description}
                      Source:        {MapSource(skill.Source)} ({skill.SourcePath})
                      Status:        {status}
                      Size:          ~{EstimateTokens(skill.Content.Length):N0} tokens
                      License:       {skill.License ?? "--"}
                      Allowed Tools: {allowedTools}
                      Files:         {files}
                    """;

        return new CommandResult
        {
            Title = $"Skill Info: {skill.Name}",
            Body = body,
            IsError = false
        };
    }

    private async Task<CommandResult> AddSkillAsync(SkillTool skillTool, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var skillName = args.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(skillName))
            return Error("Missing Argument", "Usage: /skills add <name>");

        var state = BuildState(skillTool);
        var skill = state.AllSkills.FirstOrDefault(s => string.Equals(s.Name, skillName, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
            return Error("Skill Not Found", $"Skill '{skillName}' was not found.");

        if (state.Resolution.Denied.Any(s => string.Equals(s.Name, skill.Name, StringComparison.OrdinalIgnoreCase)))
            return Error("Skill Denied", $"Skill '{skill.Name}' is not available for this agent.");

        if (state.Resolution.Loaded.Any(s => string.Equals(s.Name, skill.Name, StringComparison.OrdinalIgnoreCase)))
            return Error("Already Loaded", $"Skill '{skill.Name}' is already loaded.");

        var explicitLoaded = skillTool.SessionLoadedSkills.ToList();
        explicitLoaded.Add(skill.Name);
        var attempt = SkillResolver.Resolve(state.AllSkills, skillTool.Config, explicitLoaded);
        if (!attempt.Loaded.Any(s => string.Equals(s.Name, skill.Name, StringComparison.OrdinalIgnoreCase)))
            return Error("Budget Exceeded", $"Skill '{skill.Name}' cannot be loaded within configured limits.");

        await skillTool.ExecuteAsync(
            toolCallId: "skills-command-add",
            arguments: new Dictionary<string, object?> { ["action"] = "load", ["skillName"] = skill.Name },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!skillTool.SessionLoadedSkills.Contains(skill.Name))
            return Error("Load Failed", $"Skill '{skill.Name}' could not be loaded.");

        return new CommandResult
        {
            Title = $"Skill Loaded: {skill.Name}",
            Body = $"Loaded '{skill.Name}' (~{EstimateTokens(skill.Content.Length):N0} tokens).",
            IsError = false
        };
    }

    private static CommandResult RemoveSkill(SkillTool skillTool, IReadOnlyList<string> args)
    {
        var skillName = args.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(skillName))
            return Error("Missing Argument", "Usage: /skills remove <name>");

        if (!skillTool.TryUnload(skillName))
            return Error("Not Loaded", $"Skill '{skillName}' is not explicitly loaded in this session.");

        return new CommandResult
        {
            Title = $"Skill Removed: {skillName}",
            Body = $"Removed '{skillName}' from session-loaded skills. It will not be injected on future turns unless re-added.",
            IsError = false
        };
    }

    private CommandResult ReloadSkills(SkillTool skillTool)
    {
        var current = skillTool.GetDiscoveredSkills();
        var currentSnapshot = BuildSnapshot(current);

        Dictionary<string, SkillSnapshot>? previous;
        lock (_snapshotLock)
        {
            previous = _lastDiscovery;
            _lastDiscovery = currentSnapshot;
        }

        if (previous is null)
        {
            return new CommandResult
            {
                Title = "Skills Reloaded",
                Body = $"Skill reload complete. Discovered {current.Count} skills. No previous snapshot available for diff.",
                IsError = false
            };
        }

        var added = currentSnapshot.Keys.Except(previous.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var removed = previous.Keys.Except(currentSnapshot.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var changed = currentSnapshot
            .Where(pair => previous.TryGetValue(pair.Key, out var prior) && !prior.Equals(pair.Value))
            .Select(pair => pair.Key)
            .OrderBy(x => x)
            .ToList();

        var body = new StringBuilder();
        body.AppendLine("Skill discovery refreshed.");
        body.AppendLine($"- Total: {current.Count}");
        body.AppendLine($"- Added: {added.Count}{FormatListSuffix(added)}");
        body.AppendLine($"- Removed: {removed.Count}{FormatListSuffix(removed)}");
        body.AppendLine($"- Updated: {changed.Count}{FormatListSuffix(changed)}");

        return new CommandResult
        {
            Title = "Skills Reloaded",
            Body = body.ToString().TrimEnd(),
            IsError = false
        };
    }

    private SkillState BuildState(SkillTool skillTool)
    {
        var all = skillTool.GetDiscoveredSkills()
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var resolution = SkillResolver.Resolve(all, skillTool.Config, skillTool.SessionLoadedSkills.ToList());

        lock (_snapshotLock)
        {
            _lastDiscovery = BuildSnapshot(all);
        }

        return new SkillState(all, resolution);
    }

    private static void AppendSkillGroup(StringBuilder sb, string label, IReadOnlyList<SkillDefinition> skills)
    {
        sb.AppendLine($"  {label} ({skills.Count}):");
        if (skills.Count == 0)
        {
            sb.AppendLine("    (none)");
            return;
        }

        foreach (var skill in skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"    {skill.Name.PadRight(30)} {Truncate(skill.Description, 72)}");
    }

    private static void AppendDeniedGroup(StringBuilder sb, IReadOnlyList<SkillDefinition> skills)
    {
        sb.AppendLine($"  Denied ({skills.Count}):");
        if (skills.Count == 0)
        {
            sb.AppendLine("    (none)");
            return;
        }

        foreach (var skill in skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"    {skill.Name.PadRight(30)} Disabled by agent config");
    }

    private string DescribeSkillFiles(string sourcePath)
    {
        if (!_fileSystem.Directory.Exists(sourcePath))
            return "--";

        var entries = _fileSystem.Directory
            .GetFileSystemEntries(sourcePath)
            .Select(entry =>
            {
                var name = Path.GetRelativePath(sourcePath, entry);
                return _fileSystem.Directory.Exists(entry) ? $"{name}/" : name;
            })
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        return entries.Count == 0 ? "--" : string.Join(", ", entries);
    }

    private static Dictionary<string, SkillSnapshot> BuildSnapshot(IReadOnlyList<SkillDefinition> skills)
        => skills.ToDictionary(
            keySelector: skill => skill.Name,
            elementSelector: skill => new SkillSnapshot(skill.SourcePath, skill.Source, skill.Content.Length, skill.Description),
            comparer: StringComparer.OrdinalIgnoreCase);

    private static string MapSource(SkillSource source)
        => source switch
        {
            SkillSource.Global => "Global",
            SkillSource.Agent => "Agent",
            SkillSource.Workspace => "Workspace",
            _ => source.ToString()
        };

    private static int EstimateTokens(int chars) => (int)Math.Ceiling(chars / 4.0d);

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "--";
        if (text.Length <= maxLength)
            return text;
        return text[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static string FormatThousands(int value)
    {
        if (value == int.MaxValue)
            return "unlimited";
        if (value >= 1000)
        {
            var decimalValue = value / 1000d;
            var format = decimalValue >= 10 ? "0.#" : "0.##";
            return $"{decimalValue.ToString(format)}K";
        }

        return value.ToString();
    }

    private static string FormatListSuffix(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
            return string.Empty;

        var preview = string.Join(", ", items.Take(8));
        if (items.Count > 8)
            preview += ", ...";

        return $" ({preview})";
    }

    private static CommandResult Error(string title, string body) => new()
    {
        Title = title,
        Body = body,
        IsError = true
    };

    private sealed record SkillState(IReadOnlyList<SkillDefinition> AllSkills, SkillResolution Resolution);
    private sealed record SkillSnapshot(string SourcePath, SkillSource Source, int ContentLength, string Description);
}

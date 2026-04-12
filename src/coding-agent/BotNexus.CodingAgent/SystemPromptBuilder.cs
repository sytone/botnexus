using System.Text;
using BotNexus.Prompts;

namespace BotNexus.CodingAgent;

public sealed record ToolPromptContribution(
    string Name,
    string? Snippet = null,
    IReadOnlyList<string>? Guidelines = null);

public sealed record PromptContextFile(
    string Path,
    string Content);

public sealed record SystemPromptContext(
    string WorkingDirectory,
    string? GitBranch,
    string? GitStatus,
    string PackageManager,
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string> Skills,
    string? CustomInstructions,
    string? CustomPrompt = null,
    string? AppendSystemPrompt = null,
    IReadOnlyList<ToolPromptContribution>? ToolContributions = null,
    IReadOnlyList<PromptContextFile>? ContextFiles = null,
    DateTimeOffset? CurrentDateTime = null);

public sealed class SystemPromptBuilder
{
    public string Build(SystemPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var toolRegistry = new ToolNameRegistry(context.ToolNames);
        var normalizedWorkingDirectory = context.WorkingDirectory.Replace('\\', '/');
        var timestamp = (context.CurrentDateTime ?? DateTimeOffset.Now).ToString("O");
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(context.CustomPrompt))
        {
            builder.Append(context.CustomPrompt.Trim());
        }
        else
        {
            var sections = new List<(string? Title, string Content)>
            {
                (null, "You are a coding assistant with access to tools for reading, writing, and editing files, and executing shell commands."),
                ("Environment", BuildEnvironmentSection(context.WorkingDirectory, context.GitBranch, context.GitStatus, context.PackageManager)),
                ("Available Tools", BuildToolsSection(context)),
                ("Tool Guidelines", BuildToolGuidelinesSection(toolRegistry, context.ToolContributions))
            };

            builder.Append(BuildDocument(sections));
        }

        if (!string.IsNullOrWhiteSpace(context.AppendSystemPrompt))
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.Append(context.AppendSystemPrompt.Trim());
        }

        var contextFilesSection = BuildContextFilesSection(context.ContextFiles ?? []);
        if (!string.IsNullOrWhiteSpace(contextFilesSection))
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("## Project Context");
            builder.Append(contextFilesSection);
        }

        var hasReadTool = toolRegistry.Contains("read");
        var skillsSection = hasReadTool ? BuildSkillsSection(context.Skills) : string.Empty;
        if (!string.IsNullOrWhiteSpace(skillsSection))
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("## Skills");
            builder.Append(skillsSection);
        }

        if (!string.IsNullOrWhiteSpace(context.CustomInstructions))
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("## Custom Instructions");
            builder.Append(context.CustomInstructions.Trim());
        }

        builder.AppendLine();
        builder.Append($"Current date/time: {timestamp}");
        builder.AppendLine();
        builder.Append($"Current working directory: {normalizedWorkingDirectory}");

        return builder.ToString().TrimEnd();
    }

    private static string BuildDocument(IEnumerable<(string? Title, string Content)> sections)
    {
        var builder = new StringBuilder();
        var first = true;
        foreach (var (title, content) in sections)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            if (!first)
            {
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                builder.AppendLine($"## {title}");
            }

            builder.AppendLine(content.TrimEnd());
            first = false;
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildEnvironmentSection(string workingDirectory, string? gitBranch, string? gitStatus, string packageManager)
    {
        var lines = EnvironmentInfo.BuildSection(workingDirectory, gitBranch, gitStatus, packageManager);
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildToolsSection(SystemPromptContext context)
    {
        var contributions = context.ToolContributions ?? context.ToolNames.Select(name => new ToolPromptContribution(name)).ToList();
        if (contributions.Count == 0)
        {
            return "none";
        }

        return string.Join(
            Environment.NewLine,
            contributions.Select(contribution =>
                $"- {contribution.Name}: {contribution.Snippet ?? "Available for coding workflow tasks."}"));
    }

    private static string BuildToolGuidelinesSection(ToolNameRegistry toolRegistry, IReadOnlyList<ToolPromptContribution>? toolContributions)
    {
        var guidelines = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Use tools proactively.",
            "Read files before editing.",
            "Make precise edits.",
            "Verify changes compile.",
            "Be concise in your responses.",
            "Show file paths clearly when working with files."
        };

        var hasBash = toolRegistry.Contains("bash");
        var hasGrep = toolRegistry.Contains("grep");
        var hasFind = toolRegistry.Contains("find") || toolRegistry.Contains("glob");
        var hasListDirectory = toolRegistry.Contains("ls") || toolRegistry.Contains("list_directory");

        if (hasBash && !hasGrep && !hasFind && !hasListDirectory)
        {
            guidelines.Add("Use bash for file operations like ls, rg, find.");
        }
        else if (hasBash && (hasGrep || hasFind || hasListDirectory))
        {
            guidelines.Add("Prefer grep/find/ls tools over bash for file exploration (faster, respects .gitignore).");
        }

        foreach (var guideline in (toolContributions ?? []).SelectMany(contribution => contribution.Guidelines ?? []))
        {
            if (!string.IsNullOrWhiteSpace(guideline))
            {
                guidelines.Add(guideline.Trim());
            }
        }

        return string.Join(Environment.NewLine, guidelines.Select(guideline => $"- {guideline}"));
    }

    private static string BuildContextFilesSection(IReadOnlyList<PromptContextFile> contextFiles)
    {
        if (contextFiles.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var file in contextFiles)
        {
            if (string.IsNullOrWhiteSpace(file.Content))
            {
                continue;
            }

            builder.AppendLine($"### {file.Path}");
            builder.AppendLine(file.Content.Trim());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildSkillsSection(IReadOnlyList<string> skills)
    {
        if (skills.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var skill in skills)
        {
            var parsed = SkillsParser.Parse(skill);
            builder.AppendLine("---");
            builder.AppendLine($"name: {parsed.Name}");
            if (!string.IsNullOrWhiteSpace(parsed.Description))
            {
                builder.AppendLine($"description: {parsed.Description}");
            }

            builder.AppendLine("---");
            builder.AppendLine(parsed.Content.Trim());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

}

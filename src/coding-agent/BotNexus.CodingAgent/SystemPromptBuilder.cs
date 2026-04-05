using System.Runtime.InteropServices;
using System.Text;

namespace BotNexus.CodingAgent;

public sealed record SystemPromptContext(
    string WorkingDirectory,
    string? GitBranch,
    string? GitStatus,
    string PackageManager,
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string> Skills,
    string? CustomInstructions);

public sealed class SystemPromptBuilder
{
    public string Build(SystemPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var builder = new StringBuilder();
        builder.AppendLine("You are a coding assistant with access to tools for reading, writing, and editing files, and executing shell commands.")
            .AppendLine()
            .AppendLine("## Environment")
            .AppendLine($"- OS: {RuntimeInformation.OSDescription}")
            .AppendLine($"- Working directory: {context.WorkingDirectory}")
            .AppendLine($"- Git branch: {context.GitBranch ?? "N/A"}")
            .AppendLine($"- Git status: {context.GitStatus ?? "N/A"}")
            .AppendLine($"- Package manager: {context.PackageManager}")
            .AppendLine($"- Tools: {FormatList(context.ToolNames)}")
            .AppendLine()
            .AppendLine("## Tool Guidelines")
            .AppendLine("- Use tools proactively.")
            .AppendLine("- Read files before editing.")
            .AppendLine("- Make precise edits.")
            .AppendLine("- Verify changes compile.");

        if (context.Skills.Count > 0)
        {
            builder.AppendLine()
                .AppendLine("## Skills")
                .AppendLine(string.Join(Environment.NewLine + Environment.NewLine, context.Skills));
        }

        if (!string.IsNullOrWhiteSpace(context.CustomInstructions))
        {
            builder.AppendLine()
                .AppendLine("## Custom Instructions")
                .AppendLine(context.CustomInstructions.Trim());
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatList(IReadOnlyList<string> values)
    {
        return values.Count == 0
            ? "none"
            : string.Join(", ", values);
    }
}

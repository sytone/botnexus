using System.Text;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Builds the system prompt section for loaded and available skills.
/// </summary>
public static class SkillPromptBuilder
{
    public static string Build(
        IReadOnlyList<SkillDefinition> loaded,
        IReadOnlyList<SkillDefinition> available)
    {
        if (loaded.Count == 0 && available.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("<!-- SKILLS_CONTEXT -->");

        if (loaded.Count > 0)
        {
            sb.AppendLine("## Active Skills");
            sb.AppendLine("The following skills are loaded and active:");
            foreach (var skill in loaded)
            {
                var desc = string.IsNullOrWhiteSpace(skill.Description) ? "" : $": {skill.Description}";
                sb.AppendLine($"- **{skill.Name}**{desc}");
            }
            sb.AppendLine();

            foreach (var skill in loaded)
            {
                sb.AppendLine($"## Skill: {skill.Name}");
                sb.AppendLine();
                sb.AppendLine(SanitizeSkillContent(skill.Content));
                sb.AppendLine();
            }
        }

        if (available.Count > 0)
        {
            sb.AppendLine("## Skills Available (not loaded)");
            sb.AppendLine("Use the `skills` tool with action `load` to activate a skill when needed.");
            foreach (var skill in available)
            {
                var desc = string.IsNullOrWhiteSpace(skill.Description) ? "" : $": {skill.Description}";
                sb.AppendLine($"- **{skill.Name}**{desc}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("<!-- END_SKILLS_CONTEXT -->");
        return sb.ToString();
    }

    /// <summary>
    /// Strips sentinel markers from skill content to prevent prompt injection.
    /// </summary>
    private static string SanitizeSkillContent(string content)
    {
        return content
            .Replace("<!-- SKILLS_CONTEXT -->", "", StringComparison.OrdinalIgnoreCase)
            .Replace("<!-- END_SKILLS_CONTEXT -->", "", StringComparison.OrdinalIgnoreCase);
    }
}

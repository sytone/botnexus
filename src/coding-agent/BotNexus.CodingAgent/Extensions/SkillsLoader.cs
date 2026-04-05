namespace BotNexus.CodingAgent.Extensions;

/// <summary>
/// Loads markdown skill instructions that are injected into system prompts.
/// </summary>
public sealed class SkillsLoader
{
    public IReadOnlyList<string> LoadSkills(string workingDirectory, CodingAgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return [];
        }

        var root = Path.GetFullPath(workingDirectory);
        var skillDocuments = new List<string>();

        TryAddFile(Path.Combine(root, "AGENTS.md"), skillDocuments);
        TryAddFile(Path.Combine(root, ".botnexus-agent", "AGENTS.md"), skillDocuments);

        var skillsDirectory = Path.Combine(root, ".botnexus-agent", "skills");
        if (Directory.Exists(skillsDirectory))
        {
            foreach (var skillPath in Directory.EnumerateFiles(skillsDirectory, "*.md", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                TryAddFile(skillPath, skillDocuments);
            }
        }

        return skillDocuments;
    }

    private static void TryAddFile(string path, ICollection<string> target)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var content = File.ReadAllText(path);
        if (!string.IsNullOrWhiteSpace(content))
        {
            target.Add(content);
        }
    }
}

namespace BotNexus.Skills;

/// <summary>
/// Discovers skills from multiple filesystem paths with priority-based merging.
/// Validates skills per the Agent Skills spec before including them.
/// </summary>
public static class SkillDiscovery
{
    private const string SkillFileName = "SKILL.md";
    private const int MaxDescriptionLength = 1024;

    public static IReadOnlyList<SkillDefinition> Discover(
        string? globalSkillsDir,
        string? agentSkillsDir,
        string? workspaceSkillsDir)
    {
        var skills = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);

        ScanDirectory(skills, globalSkillsDir, SkillSource.Global);
        ScanDirectory(skills, agentSkillsDir, SkillSource.Agent);
        ScanDirectory(skills, workspaceSkillsDir, SkillSource.Workspace);

        return skills.Values.ToList();
    }

    private static void ScanDirectory(Dictionary<string, SkillDefinition> skills, string? directory, SkillSource source)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        foreach (var skillDir in Directory.GetDirectories(directory))
        {
            var skillMdPath = Path.Combine(skillDir, SkillFileName);
            if (!File.Exists(skillMdPath))
                continue;

            var dirName = Path.GetFileName(skillDir);
            try
            {
                var content = File.ReadAllText(skillMdPath);
                var skill = SkillParser.Parse(dirName, content, skillDir, source);

                if (!Validate(skill, dirName))
                    continue;

                skills[skill.Name] = skill;
            }
            catch
            {
                // Skip malformed skills
            }
        }
    }

    private static bool Validate(SkillDefinition skill, string directoryName)
    {
        // Name must be valid per spec
        if (!SkillParser.IsValidName(skill.Name))
            return false;

        // Name must match directory name
        if (!string.Equals(skill.Name, directoryName, StringComparison.Ordinal))
            return false;

        // Description is required
        if (string.IsNullOrWhiteSpace(skill.Description))
            return false;

        // Description length limit
        if (skill.Description.Length > MaxDescriptionLength)
            return false;

        return true;
    }
}

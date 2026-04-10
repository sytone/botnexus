using BotNexus.Extensions.Skills.Security;
using System.IO.Abstractions;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Discovers skills from multiple filesystem paths with priority-based merging.
/// Validates skills per the Agent Skills spec before including them.
/// </summary>
public static class SkillDiscovery
{
    private const string SkillFileName = "SKILL.md";
    private const int MaxDescriptionLength = 1024;
    private const int MaxCompatibilityLength = 500;
    private const long MaxSkillFileBytes = 524_288; // 512 KB

    public static IReadOnlyList<SkillDefinition> Discover(
        string? globalSkillsDir,
        string? agentSkillsDir,
        string? workspaceSkillsDir,
        IFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? new FileSystem();
        var skills = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);

        ScanDirectory(skills, globalSkillsDir, SkillSource.Global, fs);
        ScanDirectory(skills, agentSkillsDir, SkillSource.Agent, fs);
        ScanDirectory(skills, workspaceSkillsDir, SkillSource.Workspace, fs);

        return skills.Values.ToList();
    }

    private static void ScanDirectory(Dictionary<string, SkillDefinition> skills, string? directory, SkillSource source, IFileSystem fileSystem)
    {
        if (string.IsNullOrWhiteSpace(directory) || !fileSystem.Directory.Exists(directory))
            return;

        foreach (var skillDir in fileSystem.Directory.GetDirectories(directory))
        {
            var skillMdPath = Path.Combine(skillDir, SkillFileName);
            if (!fileSystem.File.Exists(skillMdPath))
                continue;

            var dirName = Path.GetFileName(skillDir);
            try
            {
                var skillFileInfo = fileSystem.FileInfo.New(skillMdPath);
                if (skillFileInfo.Length > MaxSkillFileBytes)
                    continue;

                var content = fileSystem.File.ReadAllText(skillMdPath);
                var skill = SkillParser.Parse(dirName, content, skillDir, source);

                if (!Validate(skill, dirName))
                    continue;

                // Security scan: block skills with critical findings
                var scanSummary = SkillSecurityScanner.ScanDirectory(skillDir, fileSystem: fileSystem);
                if (scanSummary.Critical > 0)
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
        if (!SkillParser.IsValidName(skill.Name))
            return false;

        if (!string.Equals(skill.Name, directoryName, StringComparison.Ordinal))
            return false;

        if (string.IsNullOrWhiteSpace(skill.Description))
            return false;

        if (skill.Description.Length > MaxDescriptionLength)
            return false;

        // Per spec: compatibility must be 1-500 characters if provided
        if (skill.Compatibility is not null && skill.Compatibility.Length > MaxCompatibilityLength)
            return false;

        // Skills with disable-model-invocation are excluded from model context
        if (skill.DisableModelInvocation)
            return false;

        return true;
    }
}

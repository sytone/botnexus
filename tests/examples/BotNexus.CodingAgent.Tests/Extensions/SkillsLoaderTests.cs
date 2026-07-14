using BotNexus.CodingAgent.Extensions;

namespace BotNexus.CodingAgent.Tests.Extensions;

public sealed class SkillsLoaderTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-skillsloader-{Guid.NewGuid():N}");

    public SkillsLoaderTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void LoadSkills_WhenMarkdownIsNotSkillFile_IgnoresIt()
    {
        var skillsRoot = Path.Combine(_tempDirectory, ".botnexus-agent", "skills");
        Directory.CreateDirectory(skillsRoot);
        File.WriteAllText(Path.Combine(skillsRoot, "note.md"), "not a skill");

        var skills = new SkillsLoader().LoadSkills(_tempDirectory, new CodingAgentConfig());

        skills.ShouldNotContain(skill => skill.Contains("not a skill", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadSkills_WhenSkillUsesFrontmatter_UsesDeclaredNameAndDescription()
    {
        var skillDirectory = Path.Combine(_tempDirectory, ".botnexus-agent", "skills", "example");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), """
            ---
            name: example
            description: Sample skill
            disable-model-invocation: false
            ---
            Skill body
            """);

        var skills = new SkillsLoader().LoadSkills(_tempDirectory, new CodingAgentConfig());

        skills.ShouldContain(skill =>
            skill.Contains("name: example", StringComparison.Ordinal) &&
            skill.Contains("description: Sample skill", StringComparison.Ordinal) &&
            skill.Contains("Skill body", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadSkills_WhenSkillNameInvalid_SkipsSkill()
    {
        var skillDirectory = Path.Combine(_tempDirectory, ".botnexus-agent", "skills", "BadSkill");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), """
            ---
            name: BadSkill
            description: Invalid
            ---
            Body
            """);

        var skills = new SkillsLoader().LoadSkills(_tempDirectory, new CodingAgentConfig());

        skills.ShouldNotContain(skill => skill.Contains("name: BadSkill", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadSkills_WhenNameDoesNotMatchParentDirectory_SkipsSkill()
    {
        var skillDirectory = Path.Combine(_tempDirectory, ".botnexus-agent", "skills", "actual-name");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), """
            ---
            name: different-name
            description: Invalid
            ---
            Body
            """);

        var skills = new SkillsLoader().LoadSkills(_tempDirectory, new CodingAgentConfig());

        skills.ShouldNotContain(skill => skill.Contains("name: different-name", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadSkills_WhenNameExceedsMaxLength_SkipsSkill()
    {
        var longName = new string('a', 65);
        var skillDirectory = Path.Combine(_tempDirectory, ".botnexus-agent", "skills", longName);
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), $"""
            ---
            name: {longName}
            description: Too long
            ---
            Body
            """);

        var skills = new SkillsLoader().LoadSkills(_tempDirectory, new CodingAgentConfig());

        skills.ShouldNotContain(skill => skill.Contains($"name: {longName}", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadSkills_WhenDescriptionExceedsMaxLength_SkipsSkill()
    {
        var skillDirectory = Path.Combine(_tempDirectory, ".botnexus-agent", "skills", "long-description");
        Directory.CreateDirectory(skillDirectory);
        var longDescription = new string('d', 1025);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), $"""
            ---
            name: long-description
            description: {longDescription}
            ---
            Body
            """);

        var skills = new SkillsLoader().LoadSkills(_tempDirectory, new CodingAgentConfig());

        skills.ShouldNotContain(skill => skill.Contains("name: long-description", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadSkills_WhenDuplicateNamesExist_KeepsFirstOccurrence()
    {
        var firstDirectory = Path.Combine(_tempDirectory, ".botnexus-agent", "skills", "duplicate-skill");
        var secondRoot = Path.Combine(_tempDirectory, "custom-skills");
        var secondDirectory = Path.Combine(secondRoot, "duplicate-skill");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        File.WriteAllText(Path.Combine(firstDirectory, "SKILL.md"), """
            ---
            name: duplicate-skill
            description: First
            ---
            First body
            """);
        File.WriteAllText(Path.Combine(secondDirectory, "SKILL.md"), """
            ---
            name: duplicate-skill
            description: Second
            ---
            Second body
            """);

        var config = new CodingAgentConfig { SkillsDirectory = secondRoot };
        var skills = new SkillsLoader().LoadSkills(_tempDirectory, config);

        var duplicateSkills = skills.Where(skill => skill.Contains("name: duplicate-skill", StringComparison.Ordinal)).ToList();
        duplicateSkills.ShouldHaveSingleItem();
        duplicateSkills[0].ShouldContain("description: First");
        duplicateSkills[0].ShouldContain("First body");
    }

    [Fact]
    public void LoadSkills_SkillUnderNodeModules_IsIgnored()
    {
        var skillDirectory = Path.Combine(_tempDirectory, ".botnexus-agent", "skills", "node_modules", "ignored");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), """
            ---
            name: ignored-node-modules
            description: Should be ignored
            ---
            Body
            """);

        var skills = new SkillsLoader().LoadSkills(_tempDirectory, new CodingAgentConfig());

        skills.ShouldNotContain(skill => skill.Contains("ignored-node-modules", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadSkills_SkillUnderBin_IsIgnored()
    {
        var skillDirectory = Path.Combine(_tempDirectory, ".botnexus-agent", "skills", "bin", "ignored");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), """
            ---
            name: ignored-bin
            description: Should be ignored
            ---
            Body
            """);

        var skills = new SkillsLoader().LoadSkills(_tempDirectory, new CodingAgentConfig());

        skills.ShouldNotContain(skill => skill.Contains("ignored-bin", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadSkills_SkillInRegularDirectory_IsIncluded()
    {
        var skillDirectory = Path.Combine(_tempDirectory, ".botnexus-agent", "skills", "normal-skill");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), """
            ---
            name: normal-skill
            description: Should load
            ---
            Normal body
            """);

        var skills = new SkillsLoader().LoadSkills(_tempDirectory, new CodingAgentConfig());

        skills.ShouldContain(skill => skill.Contains("name: normal-skill", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadSkills_RespectsCustomGitignorePatterns()
    {
        var skillsDir = Path.Combine(_tempDirectory, ".botnexus-agent", "skills");
        Directory.CreateDirectory(skillsDir);
        File.WriteAllText(Path.Combine(skillsDir, ".gitignore"), "custom-ignore/\n");

        var ignoredSkillDirectory = Path.Combine(_tempDirectory, ".botnexus-agent", "skills", "custom-ignore", "hidden");
        Directory.CreateDirectory(ignoredSkillDirectory);
        File.WriteAllText(Path.Combine(ignoredSkillDirectory, "SKILL.md"), """
            ---
            name: ignored-by-gitignore
            description: Should be ignored
            ---
            Hidden body
            """);

        var visibleSkillDirectory = Path.Combine(_tempDirectory, ".botnexus-agent", "skills", "visible-skill");
        Directory.CreateDirectory(visibleSkillDirectory);
        File.WriteAllText(Path.Combine(visibleSkillDirectory, "SKILL.md"), """
            ---
            name: visible-skill
            description: Should load
            ---
            Visible body
            """);

        var skills = new SkillsLoader().LoadSkills(_tempDirectory, new CodingAgentConfig());

        skills.ShouldContain(skill => skill.Contains("visible-skill", StringComparison.Ordinal));
        skills.ShouldNotContain(skill => skill.Contains("ignored-by-gitignore", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadSkills_WhenSkillIsInDefaultIgnoredDirectory_SkipsSkill()
    {
        var ignoredSkillDirectory = Path.Combine(_tempDirectory, ".botnexus-agent", "skills", "node_modules", "pkg");
        Directory.CreateDirectory(ignoredSkillDirectory);
        File.WriteAllText(Path.Combine(ignoredSkillDirectory, "SKILL.md"), """
            ---
            name: ignored-skill
            description: Should be ignored
            ---
            Ignored body
            """);

        var allowedSkillDirectory = Path.Combine(_tempDirectory, ".botnexus-agent", "skills", "allowed-skill");
        Directory.CreateDirectory(allowedSkillDirectory);
        File.WriteAllText(Path.Combine(allowedSkillDirectory, "SKILL.md"), """
            ---
            name: allowed-skill
            description: Allowed
            ---
            Allowed body
            """);

        var skills = new SkillsLoader().LoadSkills(_tempDirectory, new CodingAgentConfig());

        skills.Where(skill => skill.Contains("name: allowed-skill", StringComparison.Ordinal)).ShouldHaveSingleItem();
        skills.ShouldNotContain(skill => skill.Contains("name: ignored-skill", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadSkills_WhenGitIgnoreNegatesDirectory_RespectsNegation()
    {
        var skillsRoot = Path.Combine(_tempDirectory, ".botnexus-agent", "skills");
        Directory.CreateDirectory(skillsRoot);
        File.WriteAllText(Path.Combine(skillsRoot, ".gitignore"), """
            ignored/
            !ignored/allowed/
            """);

        var blockedDirectory = Path.Combine(skillsRoot, "ignored", "blocked");
        Directory.CreateDirectory(blockedDirectory);
        File.WriteAllText(Path.Combine(blockedDirectory, "SKILL.md"), """
            ---
            name: blocked
            description: Blocked
            ---
            Blocked body
            """);

        var allowedDirectory = Path.Combine(skillsRoot, "ignored", "allowed");
        Directory.CreateDirectory(allowedDirectory);
        File.WriteAllText(Path.Combine(allowedDirectory, "SKILL.md"), """
            ---
            name: allowed
            description: Allowed by negation
            ---
            Allowed body
            """);

        var skills = new SkillsLoader().LoadSkills(_tempDirectory, new CodingAgentConfig());

        skills.ShouldContain(skill => skill.Contains("name: allowed", StringComparison.Ordinal));
        skills.ShouldNotContain(skill => skill.Contains("name: blocked", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadSkills_WhenDescriptionMissing_SkipsSkill()
    {
        var skillDirectory = Path.Combine(_tempDirectory, ".botnexus-agent", "skills", "missing-description");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), """
            ---
            name: missing-description
            ---
            Body
            """);

        var skills = new SkillsLoader().LoadSkills(_tempDirectory, new CodingAgentConfig());

        skills.ShouldNotContain(skill => skill.Contains("name: missing-description", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadSkills_WhenNameHasConsecutiveHyphens_SkipsSkill()
    {
        var skillDirectory = Path.Combine(_tempDirectory, ".botnexus-agent", "skills", "bad--name");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), """
            ---
            name: bad--name
            description: Invalid name
            ---
            Body
            """);

        var skills = new SkillsLoader().LoadSkills(_tempDirectory, new CodingAgentConfig());

        skills.ShouldNotContain(skill => skill.Contains("name: bad--name", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}

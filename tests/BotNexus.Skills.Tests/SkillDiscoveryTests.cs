using BotNexus.Skills;
using FluentAssertions;

namespace BotNexus.Skills.Tests;

public sealed class SkillDiscoveryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "botnexus-skill-tests", Guid.NewGuid().ToString("N"));

    public SkillDiscoveryTests() => Directory.CreateDirectory(_tempDir);

    [Fact]
    public void Discover_GlobalSkills_FindsValidSkills()
    {
        var globalDir = Path.Combine(_tempDir, "skills");
        CreateSkill(globalDir, "email-triage", "Classify emails");

        var skills = SkillDiscovery.Discover(globalDir, agentSkillsDir: null, workspaceSkillsDir: null);

        skills.Should().ContainSingle(s => s.Name == "email-triage");
        skills[0].Source.Should().Be(SkillSource.Global);
    }

    [Fact]
    public void Discover_AgentSkills_OverrideGlobal()
    {
        var globalDir = Path.Combine(_tempDir, "global");
        var agentDir = Path.Combine(_tempDir, "agent");
        CreateSkill(globalDir, "email-triage", "Global version");
        CreateSkill(agentDir, "email-triage", "Agent version");

        var skills = SkillDiscovery.Discover(globalDir, agentDir, workspaceSkillsDir: null);

        skills.Should().ContainSingle(s => s.Name == "email-triage");
        skills[0].Source.Should().Be(SkillSource.Agent);
        skills[0].Description.Should().Be("Agent version");
    }

    [Fact]
    public void Discover_WorkspaceSkills_OverrideBoth()
    {
        var globalDir = Path.Combine(_tempDir, "global");
        var agentDir = Path.Combine(_tempDir, "agent");
        var wsDir = Path.Combine(_tempDir, "workspace");
        CreateSkill(globalDir, "email-triage", "Global");
        CreateSkill(agentDir, "email-triage", "Agent");
        CreateSkill(wsDir, "email-triage", "Workspace");

        var skills = SkillDiscovery.Discover(globalDir, agentDir, wsDir);

        skills.Should().ContainSingle(s => s.Name == "email-triage");
        skills[0].Source.Should().Be(SkillSource.Workspace);
        skills[0].Description.Should().Be("Workspace");
    }

    [Fact]
    public void Discover_MultipleSkills_FromDifferentPaths()
    {
        var globalDir = Path.Combine(_tempDir, "global");
        var agentDir = Path.Combine(_tempDir, "agent");
        CreateSkill(globalDir, "email-triage", "Email");
        CreateSkill(agentDir, "calendar", "Calendar");

        var skills = SkillDiscovery.Discover(globalDir, agentDir, null);

        skills.Should().HaveCount(2);
        skills.Should().Contain(s => s.Name == "email-triage");
        skills.Should().Contain(s => s.Name == "calendar");
    }

    [Fact]
    public void Discover_NullPaths_ReturnsEmpty()
    {
        SkillDiscovery.Discover(null, null, null).Should().BeEmpty();
    }

    [Fact]
    public void Discover_NonexistentPaths_ReturnsEmpty()
    {
        SkillDiscovery.Discover("/nonexistent", null, null).Should().BeEmpty();
    }

    [Fact]
    public void Discover_MissingSkillMd_SkipsDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "skills", "empty-skill"));
        SkillDiscovery.Discover(Path.Combine(_tempDir, "skills"), null, null).Should().BeEmpty();
    }

    [Fact]
    public void Discover_InvalidSkillName_SkipsSkill()
    {
        var dir = Path.Combine(_tempDir, "skills");
        CreateSkill(dir, "Bad-Name", "Invalid name");

        var skills = SkillDiscovery.Discover(dir, null, null);

        // Name validation should skip this skill (uppercase in directory name)
        skills.Should().BeEmpty();
    }

    [Fact]
    public void Discover_NameMismatch_SkipsSkill()
    {
        var dir = Path.Combine(_tempDir, "skills", "actual-dir");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), """
            ---
            name: different-name
            description: Mismatch
            ---
            Content
            """);

        var skills = SkillDiscovery.Discover(Path.Combine(_tempDir, "skills"), null, null);

        skills.Should().BeEmpty();
    }

    [Fact]
    public void Discover_MissingDescription_SkipsSkill()
    {
        var dir = Path.Combine(_tempDir, "skills", "no-desc");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), """
            ---
            name: no-desc
            ---
            Content
            """);

        var skills = SkillDiscovery.Discover(Path.Combine(_tempDir, "skills"), null, null);

        skills.Should().BeEmpty();
    }

    private static void CreateSkill(string parentDir, string skillName, string description)
    {
        var skillDir = Path.Combine(parentDir, skillName);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), $"""
            ---
            name: {skillName}
            description: {description}
            ---
            # {skillName}

            Skill instructions.
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }
}

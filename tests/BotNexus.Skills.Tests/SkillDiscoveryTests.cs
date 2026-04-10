using BotNexus.Extensions.Skills;
using FluentAssertions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillDiscoveryTests
{
    private readonly MockFileSystem _fileSystem = new();
    private const string TempDir = @"C:\botnexus-skill-tests";

    [Fact]
    public void Discover_GlobalSkills_FindsValidSkills()
    {
        var globalDir = Path.Combine(TempDir, "skills");
        CreateSkill(globalDir, "email-triage", "Classify emails");

        var skills = SkillDiscovery.Discover(globalDir, agentSkillsDir: null, workspaceSkillsDir: null, _fileSystem);

        skills.Should().ContainSingle(s => s.Name == "email-triage");
        skills[0].Source.Should().Be(SkillSource.Global);
    }

    [Fact]
    public void Discover_AgentSkills_OverrideGlobal()
    {
        var globalDir = Path.Combine(TempDir, "global");
        var agentDir = Path.Combine(TempDir, "agent");
        CreateSkill(globalDir, "email-triage", "Global version");
        CreateSkill(agentDir, "email-triage", "Agent version");

        var skills = SkillDiscovery.Discover(globalDir, agentDir, workspaceSkillsDir: null, _fileSystem);

        skills.Should().ContainSingle(s => s.Name == "email-triage");
        skills[0].Source.Should().Be(SkillSource.Agent);
        skills[0].Description.Should().Be("Agent version");
    }

    [Fact]
    public void Discover_WorkspaceSkills_OverrideBoth()
    {
        var globalDir = Path.Combine(TempDir, "global");
        var agentDir = Path.Combine(TempDir, "agent");
        var wsDir = Path.Combine(TempDir, "workspace");
        CreateSkill(globalDir, "email-triage", "Global");
        CreateSkill(agentDir, "email-triage", "Agent");
        CreateSkill(wsDir, "email-triage", "Workspace");

        var skills = SkillDiscovery.Discover(globalDir, agentDir, wsDir, _fileSystem);

        skills.Should().ContainSingle(s => s.Name == "email-triage");
        skills[0].Source.Should().Be(SkillSource.Workspace);
        skills[0].Description.Should().Be("Workspace");
    }

    [Fact]
    public void Discover_MultipleSkills_FromDifferentPaths()
    {
        var globalDir = Path.Combine(TempDir, "global");
        var agentDir = Path.Combine(TempDir, "agent");
        CreateSkill(globalDir, "email-triage", "Email");
        CreateSkill(agentDir, "calendar", "Calendar");

        var skills = SkillDiscovery.Discover(globalDir, agentDir, null, _fileSystem);

        skills.Should().HaveCount(2);
        skills.Should().Contain(s => s.Name == "email-triage");
        skills.Should().Contain(s => s.Name == "calendar");
    }

    [Fact]
    public void Discover_NullPaths_ReturnsEmpty()
    {
        SkillDiscovery.Discover(null, null, null, _fileSystem).Should().BeEmpty();
    }

    [Fact]
    public void Discover_NonexistentPaths_ReturnsEmpty()
    {
        SkillDiscovery.Discover(@"C:\nonexistent", null, null, _fileSystem).Should().BeEmpty();
    }

    [Fact]
    public void Discover_MissingSkillMd_SkipsDirectory()
    {
        _fileSystem.Directory.CreateDirectory(Path.Combine(TempDir, "skills", "empty-skill"));
        SkillDiscovery.Discover(Path.Combine(TempDir, "skills"), null, null, _fileSystem).Should().BeEmpty();
    }

    [Fact]
    public void Discover_InvalidSkillName_SkipsSkill()
    {
        var dir = Path.Combine(TempDir, "skills");
        CreateSkill(dir, "Bad-Name", "Invalid name");

        var skills = SkillDiscovery.Discover(dir, null, null, _fileSystem);

        // Name validation should skip this skill (uppercase in directory name)
        skills.Should().BeEmpty();
    }

    [Fact]
    public void Discover_NameMismatch_SkipsSkill()
    {
        var dir = Path.Combine(TempDir, "skills", "actual-dir");
        _fileSystem.Directory.CreateDirectory(dir);
        _fileSystem.File.WriteAllText(Path.Combine(dir, "SKILL.md"), """
            ---
            name: different-name
            description: Mismatch
            ---
            Content
            """);

        var skills = SkillDiscovery.Discover(Path.Combine(TempDir, "skills"), null, null, _fileSystem);

        skills.Should().BeEmpty();
    }

    [Fact]
    public void Discover_MissingDescription_SkipsSkill()
    {
        var dir = Path.Combine(TempDir, "skills", "no-desc");
        _fileSystem.Directory.CreateDirectory(dir);
        _fileSystem.File.WriteAllText(Path.Combine(dir, "SKILL.md"), """
            ---
            name: no-desc
            ---
            Content
            """);

        var skills = SkillDiscovery.Discover(Path.Combine(TempDir, "skills"), null, null, _fileSystem);

        skills.Should().BeEmpty();
    }

    [Fact]
    public void Discover_OversizedSkillFile_SkipsSkill()
    {
        var dir = Path.Combine(TempDir, "skills", "oversized-skill");
        _fileSystem.Directory.CreateDirectory(dir);

        var oversizedBody = new string('x', 530_000);
        _fileSystem.File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"""
            ---
            name: oversized-skill
            description: Oversized
            ---
            {oversizedBody}
            """);

        var skills = SkillDiscovery.Discover(Path.Combine(TempDir, "skills"), null, null, _fileSystem);

        skills.Should().BeEmpty();
    }

    private void CreateSkill(string parentDir, string skillName, string description)
    {
        var skillDir = Path.Combine(parentDir, skillName);
        _fileSystem.Directory.CreateDirectory(skillDir);
        _fileSystem.File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), $"""
            ---
            name: {skillName}
            description: {description}
            ---
            # {skillName}

            Skill instructions.
            """);
    }

}

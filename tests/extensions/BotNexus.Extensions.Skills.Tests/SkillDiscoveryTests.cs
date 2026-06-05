using BotNexus.Extensions.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillDiscoveryTests
{
    private readonly MockFileSystem _fileSystem = new();
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "botnexus-skill-tests");

    [Fact]
    public void Discover_GlobalSkills_FindsValidSkills()
    {
        var globalDir = Path.Combine(TempDir, "skills");
        CreateSkill(globalDir, "email-triage", "Classify emails");

        var skills = SkillDiscovery.Discover(globalDir, agentSkillsDir: null, workspaceSkillsDir: null, _fileSystem);

        skills.Where(s => s.Name == "email-triage").ShouldHaveSingleItem();
        skills[0].Source.ShouldBe(SkillSource.Global);
    }

    [Fact]
    public void Discover_AgentSkills_OverrideGlobal()
    {
        var globalDir = Path.Combine(TempDir, "global");
        var agentDir = Path.Combine(TempDir, "agent");
        CreateSkill(globalDir, "email-triage", "Global version");
        CreateSkill(agentDir, "email-triage", "Agent version");

        var skills = SkillDiscovery.Discover(globalDir, agentDir, workspaceSkillsDir: null, _fileSystem);

        skills.Where(s => s.Name == "email-triage").ShouldHaveSingleItem();
        skills[0].Source.ShouldBe(SkillSource.Agent);
        skills[0].Description.ShouldBe("Agent version");
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

        skills.Where(s => s.Name == "email-triage").ShouldHaveSingleItem();
        skills[0].Source.ShouldBe(SkillSource.Workspace);
        skills[0].Description.ShouldBe("Workspace");
    }

    [Fact]
    public void Discover_MultipleSkills_FromDifferentPaths()
    {
        var globalDir = Path.Combine(TempDir, "global");
        var agentDir = Path.Combine(TempDir, "agent");
        CreateSkill(globalDir, "email-triage", "Email");
        CreateSkill(agentDir, "calendar", "Calendar");

        var skills = SkillDiscovery.Discover(globalDir, agentDir, null, _fileSystem);

        skills.Count().ShouldBe(2);
        skills.ShouldContain(s => s.Name == "email-triage");
        skills.ShouldContain(s => s.Name == "calendar");
    }

    [Fact]
    public void Discover_NullPaths_ReturnsEmpty()
    {
        SkillDiscovery.Discover(null, null, null, _fileSystem).ShouldBeEmpty();
    }

    [Fact]
    public void Discover_NonexistentPaths_ReturnsEmpty()
    {
        SkillDiscovery.Discover(Path.Combine(Path.GetTempPath(), "nonexistent"), null, null, _fileSystem).ShouldBeEmpty();
    }

    [Fact]
    public void Discover_MissingSkillMd_SkipsDirectory()
    {
        _fileSystem.Directory.CreateDirectory(Path.Combine(TempDir, "skills", "empty-skill"));
        SkillDiscovery.Discover(Path.Combine(TempDir, "skills"), null, null, _fileSystem).ShouldBeEmpty();
    }

    [Fact]
    public void Discover_InvalidSkillName_SkipsSkill()
    {
        var dir = Path.Combine(TempDir, "skills");
        CreateSkill(dir, "Bad-Name", "Invalid name");

        var skills = SkillDiscovery.Discover(dir, null, null, _fileSystem);

        // Name validation should skip this skill (uppercase in directory name)
        skills.ShouldBeEmpty();
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

        skills.ShouldBeEmpty();
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

        skills.ShouldBeEmpty();
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

        skills.ShouldBeEmpty();
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

    // ── warning-log tests (issue #870) ───────────────────────────────────────

    [Fact]
    public void Discover_SkillWithMissingDescription_EmitsWarning()
    {
        // A SKILL.md with a name but no description should be skipped with a LogWarning.
        var dir = Path.Combine(TempDir, "warn-no-desc");
        var skillDir = Path.Combine(dir, "my-skill");
        _fileSystem.Directory.CreateDirectory(skillDir);
        _fileSystem.File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: my-skill\n---\nBody without description.");

        var logger = new CapturingLogger();
        var skills = SkillDiscovery.Discover(dir, null, null, _fileSystem, logger);

        skills.ShouldBeEmpty();
        logger.Warnings.ShouldContain(w => w.Contains("my-skill") && w.Contains("description"));
    }

    [Fact]
    public void Discover_SkillWithInvalidName_EmitsWarning()
    {
        // Directory name "bad name" (space) should fail SkillParser.IsValidName.
        var dir = Path.Combine(TempDir, "warn-bad-name");
        var skillDir = Path.Combine(dir, "bad name");
        _fileSystem.Directory.CreateDirectory(skillDir);
        _fileSystem.File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: bad name\ndescription: desc\n---\nBody.");

        var logger = new CapturingLogger();
        var skills = SkillDiscovery.Discover(dir, null, null, _fileSystem, logger);

        skills.ShouldBeEmpty();
        logger.Warnings.ShouldNotBeEmpty("Expected a warning for invalid skill name");
    }

    [Fact]
    public void Discover_ValidSkill_EmitsNoWarning()
    {
        // A well-formed skill must never emit any warnings.
        var dir = Path.Combine(TempDir, "warn-valid");
        CreateSkill(dir, "my-skill", "A valid description");

        var logger = new CapturingLogger();
        var skills = SkillDiscovery.Discover(dir, null, null, _fileSystem, logger);

        skills.ShouldHaveSingleItem();
        logger.Warnings.ShouldBeEmpty("No warnings expected for a valid skill");
    }

    [Fact]
    public void Discover_SkillWithNullLogger_DoesNotThrow()
    {
        // Passing null for logger must be safe (backward compat with all existing call sites).
        var dir = Path.Combine(TempDir, "warn-null-logger");
        var skillDir = Path.Combine(dir, "my-skill");
        _fileSystem.Directory.CreateDirectory(skillDir);
        // Malformed SKILL.md so a warning would normally fire if logger were wired
        _fileSystem.File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: my-skill\n---\nNo description.");

        // Should not throw even though skill is invalid and logger is null
        Should.NotThrow(() => SkillDiscovery.Discover(dir, null, null, _fileSystem, logger: null));
    }

}

/// <summary>
/// Minimal logger that captures Warning-level messages for test assertions.
/// </summary>
public sealed class CapturingLogger : ILogger
{
    public List<string> Warnings { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
            Warnings.Add(formatter(state, exception));
    }
}

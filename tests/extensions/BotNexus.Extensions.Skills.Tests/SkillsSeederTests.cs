using BotNexus.Extensions.Skills;
using Shouldly;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillsSeederTests
{
    [Fact]
    public void EnsureGlobalSkillsSeed_CreatesDirectoryAndExampleSkillWhenMissing()
    {
        var fs = new MockFileSystem();
        var globalDir = "/skills";

        SkillsSeeder.EnsureGlobalSkillsSeed(globalDir, fs);

        fs.Directory.Exists(globalDir).ShouldBeTrue();
        var skillFile = Path.Combine(globalDir, "example-skill", "SKILL.md");
        fs.File.Exists(skillFile).ShouldBeTrue();
        var content = fs.File.ReadAllText(skillFile);
        content.ShouldContain("name: example-skill");
        content.ShouldContain("description:");
    }

    [Fact]
    public void EnsureGlobalSkillsSeed_SeedsWhenDirectoryExistsButHasNoSkills()
    {
        var fs = new MockFileSystem();
        var globalDir = "/skills";
        fs.Directory.CreateDirectory(globalDir);
        // Empty directory, no subdirs

        SkillsSeeder.EnsureGlobalSkillsSeed(globalDir, fs);

        var skillFile = Path.Combine(globalDir, "example-skill", "SKILL.md");
        fs.File.Exists(skillFile).ShouldBeTrue();
    }

    [Fact]
    public void EnsureGlobalSkillsSeed_IsIdempotentWhenExampleAlreadyExists()
    {
        var fs = new MockFileSystem();
        var globalDir = "/skills";
        var exampleDir = Path.Combine(globalDir, "example-skill");
        var skillFile = Path.Combine(exampleDir, "SKILL.md");
        fs.Directory.CreateDirectory(exampleDir);
        fs.File.WriteAllText(skillFile, "original content");

        SkillsSeeder.EnsureGlobalSkillsSeed(globalDir, fs);

        // Must not overwrite
        fs.File.ReadAllText(skillFile).ShouldBe("original content");
    }

    [Fact]
    public void EnsureGlobalSkillsSeed_DoesNotSeedWhenExistingSkillsPresent()
    {
        var fs = new MockFileSystem();
        var globalDir = "/skills";
        // Existing skill (not example-skill)
        var existingDir = Path.Combine(globalDir, "my-existing-skill");
        fs.Directory.CreateDirectory(existingDir);
        fs.File.WriteAllText(Path.Combine(existingDir, "SKILL.md"), "---\nname: my-existing-skill\ndescription: existing\n---\n");

        SkillsSeeder.EnsureGlobalSkillsSeed(globalDir, fs);

        // example-skill should NOT have been created
        var exampleFile = Path.Combine(globalDir, "example-skill", "SKILL.md");
        fs.File.Exists(exampleFile).ShouldBeFalse();
    }

    [Fact]
    public void EnsureGlobalSkillsSeed_NoOpsOnNullOrEmptyPath()
    {
        var fs = new MockFileSystem();
        // Must not throw
        SkillsSeeder.EnsureGlobalSkillsSeed(null, fs);
        SkillsSeeder.EnsureGlobalSkillsSeed(string.Empty, fs);
        SkillsSeeder.EnsureGlobalSkillsSeed("   ", fs);
    }
}

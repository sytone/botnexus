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

    [Fact]
    public void EnsureSharedPromptSeed_CopiesBundledOptimizationPromptWhenMissing()
    {
        var fs = new MockFileSystem();
        const string promptsDir = "/prompts";

        SkillsPromptSeeder.EnsureSharedPromptSeed(promptsDir, fs);

        var promptPath = Path.Combine(promptsDir, "optimize-shared-skills.prompt.md");
        fs.File.Exists(promptPath).ShouldBeTrue();
        var content = fs.File.ReadAllText(promptPath);
        content.ShouldContain("name: optimize-shared-skills");
        content.ShouldContain("GPT-6 Sol");
        content.ShouldContain("GPT-6 Luna");
        content.ShouldContain("Do not modify any skill during the audit phase");
    }

    [Fact]
    public void EnsureSharedPromptSeed_DoesNotOverwriteExistingPrompt()
    {
        var fs = new MockFileSystem();
        const string promptsDir = "/prompts";
        var promptPath = Path.Combine(promptsDir, "optimize-shared-skills.prompt.md");
        fs.Directory.CreateDirectory(promptsDir);
        fs.File.WriteAllText(promptPath, "operator customization");

        SkillsPromptSeeder.EnsureSharedPromptSeed(promptsDir, fs);

        fs.File.ReadAllText(promptPath).ShouldBe("operator customization");
    }

    [Fact]
    public void EnsureSharedPromptSeed_NoOpsOnNullOrEmptyPath()
    {
        var fs = new MockFileSystem();

        SkillsPromptSeeder.EnsureSharedPromptSeed(null, fs);
        SkillsPromptSeeder.EnsureSharedPromptSeed(string.Empty, fs);
        SkillsPromptSeeder.EnsureSharedPromptSeed("   ", fs);

        fs.AllFiles.ShouldBeEmpty();
    }
}

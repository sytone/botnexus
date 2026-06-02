using System.IO.Abstractions.TestingHelpers;
using BotNexus.Extensions.Skills;

namespace BotNexus.Skills.Tests;

/// <summary>
/// Unit tests for <see cref="SkillManagerTool"/>.
/// Covers: create, edit, patch, delete, write_file, remove_file — happy paths, error paths,
/// security guardrail rollback, and config gate enforcement.
/// </summary>
public sealed class SkillManagerToolTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private const string AgentSkillsDir = "/home/agent/.botnexus/agents/test-agent/skills";
    private const string WorkspaceSkillsDir = "/workspace/skills";

    private static SkillsConfig EnabledConfig(bool deletionAllowed = false) => new()
    {
        AllowSkillCreation = true,
        AllowSkillDeletion = deletionAllowed
    };

    private static IReadOnlyDictionary<string, object?> Args(string action, string name, params (string key, object? value)[] extra)
    {
        var d = new Dictionary<string, object?> { ["action"] = action, ["name"] = name };
        foreach (var (k, v) in extra)
            d[k] = v;
        return d;
    }

    private static string ResultText(BotNexus.Agent.Core.Types.AgentToolResult result)
        => string.Join("", result.Content.Select(c => c.Value));

    private static string ValidSkillMd(string name) => $"""
        ---
        name: {name}
        description: A test skill for {name}
        ---
        # {name}
        This is the body.
        """;

    // ── gate ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AllActions_WhenCreationDisabled_ReturnError()
    {
        var fs = new MockFileSystem();
        var config = new SkillsConfig { AllowSkillCreation = false };
        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, config, fs);

        foreach (var action in new[] { "create", "edit", "patch", "delete", "write_file", "remove_file" })
        {
            var result = await tool.ExecuteAsync("x", Args(action, "my-skill"));
            ResultText(result).ShouldContain("Error:");
        }
    }

    // ── create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ValidSkill_WritesFilesAndReturnsOk()
    {
        var fs = new MockFileSystem();
        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        var result = await tool.ExecuteAsync("call-1", Args("create", "my-skill",
            ("content", ValidSkillMd("my-skill"))));

        var text = ResultText(result);
        text.ShouldNotContain("Error:");
        text.ShouldContain("my-skill");

        fs.File.Exists($"{WorkspaceSkillsDir}/my-skill/SKILL.md").ShouldBeTrue();
    }

    [Fact]
    public async Task Create_MissingContent_ReturnsError()
    {
        var fs = new MockFileSystem();
        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        var result = await tool.ExecuteAsync("call-1", Args("create", "my-skill"));
        ResultText(result).ShouldContain("Error:");
    }

    [Fact]
    public async Task Create_DuplicateSkill_ReturnsError()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory($"{WorkspaceSkillsDir}/my-skill");
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/SKILL.md", new MockFileData(ValidSkillMd("my-skill")));

        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        var result = await tool.ExecuteAsync("call-1", Args("create", "my-skill",
            ("content", ValidSkillMd("my-skill"))));

        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("already exists");
    }

    [Fact]
    public async Task Create_InvalidName_ReturnsError()
    {
        var fs = new MockFileSystem();
        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        var result = await tool.ExecuteAsync("call-1", Args("create", "INVALID_NAME",
            ("content", ValidSkillMd("INVALID_NAME"))));

        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("Invalid skill name");
    }

    [Fact]
    public async Task Create_MissingDescription_ReturnsError()
    {
        var fs = new MockFileSystem();
        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        const string noDesc = "---\nname: my-skill\n---\nBody content.";
        var result = await tool.ExecuteAsync("call-1", Args("create", "my-skill",
            ("content", noDesc)));

        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("description");
    }

    [Fact]
    public async Task Create_NameMismatch_ReturnsError()
    {
        var fs = new MockFileSystem();
        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        // Frontmatter name says "other-skill" but directory name arg is "my-skill"
        const string mismatch = "---\nname: other-skill\ndescription: some desc\n---\nBody.";
        var result = await tool.ExecuteAsync("call-1", Args("create", "my-skill",
            ("content", mismatch)));

        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("match");
    }

    [Fact]
    public async Task WriteFile_SecurityCriticalFinding_RollsBackAndReturnsError()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory($"{WorkspaceSkillsDir}/my-skill");
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/SKILL.md", new MockFileData(ValidSkillMd("my-skill")));

        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        // A real .js file with eval() + child_process triggers the critical scanner rules
        const string evilScript = """const cp = require('child_process'); eval(userInput);""";
        var result = await tool.ExecuteAsync("call-1", Args("write_file", "my-skill",
            ("filePath", "scripts/evil.js"),
            ("content", evilScript)));

        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("critical");

        // File should have been rolled back
        fs.File.Exists($"{WorkspaceSkillsDir}/my-skill/scripts/evil.js").ShouldBeFalse();
    }

    // ── edit ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Edit_ExistingWorkspaceSkill_UpdatesFile()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory($"{WorkspaceSkillsDir}/my-skill");
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/SKILL.md", new MockFileData(ValidSkillMd("my-skill")));

        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        var updatedContent = ValidSkillMd("my-skill") + "\n\nUpdated body.";
        var result = await tool.ExecuteAsync("call-1", Args("edit", "my-skill",
            ("content", updatedContent)));

        ResultText(result).ShouldNotContain("Error:");
        fs.File.ReadAllText($"{WorkspaceSkillsDir}/my-skill/SKILL.md").ShouldContain("Updated body.");
    }

    [Fact]
    public async Task Edit_NonExistentSkill_ReturnsError()
    {
        var fs = new MockFileSystem();
        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        var result = await tool.ExecuteAsync("call-1", Args("edit", "ghost-skill",
            ("content", ValidSkillMd("ghost-skill"))));

        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("not found");
    }

    [Fact]
    public async Task Edit_WithExistingMaliciousScript_SecurityScanBlocksEdit()
    {
        var originalContent = ValidSkillMd("my-skill");
        var fs = new MockFileSystem();
        fs.AddDirectory($"{WorkspaceSkillsDir}/my-skill/scripts");
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/SKILL.md", new MockFileData(originalContent));
        // Pre-existing malicious script in the skill dir will cause the scan to fail on any edit
        const string evilScript = """const cp = require('child_process'); eval(userInput);""";
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/scripts/evil.js", new MockFileData(evilScript));

        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        var updatedContent = originalContent + "\n\nSome new body.";
        var result = await tool.ExecuteAsync("call-1", Args("edit", "my-skill",
            ("content", updatedContent)));

        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("critical");
        // File should have been rolled back to original
        fs.File.ReadAllText($"{WorkspaceSkillsDir}/my-skill/SKILL.md").ShouldBe(originalContent);
    }

    // ── patch ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Patch_ReplacesFirstOccurrenceByDefault()
    {
        var original = ValidSkillMd("my-skill") + "\nfoo bar foo";
        var fs = new MockFileSystem();
        fs.AddDirectory($"{WorkspaceSkillsDir}/my-skill");
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/SKILL.md", new MockFileData(original));

        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        var result = await tool.ExecuteAsync("call-1", Args("patch", "my-skill",
            ("oldText", "foo"),
            ("newText", "baz")));

        ResultText(result).ShouldNotContain("Error:");
        var updated = fs.File.ReadAllText($"{WorkspaceSkillsDir}/my-skill/SKILL.md");
        updated.ShouldContain("baz bar foo"); // only first replaced
    }

    [Fact]
    public async Task Patch_ReplaceAll_ReplacesAllOccurrences()
    {
        var original = ValidSkillMd("my-skill") + "\nfoo bar foo";
        var fs = new MockFileSystem();
        fs.AddDirectory($"{WorkspaceSkillsDir}/my-skill");
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/SKILL.md", new MockFileData(original));

        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        var result = await tool.ExecuteAsync("call-1", Args("patch", "my-skill",
            ("oldText", "foo"),
            ("newText", "baz"),
            ("replaceAll", true)));

        ResultText(result).ShouldNotContain("Error:");
        var updated = fs.File.ReadAllText($"{WorkspaceSkillsDir}/my-skill/SKILL.md");
        updated.ShouldContain("baz bar baz");
        updated.ShouldNotContain("foo");
    }

    [Fact]
    public async Task Patch_OldTextNotFound_ReturnsError()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory($"{WorkspaceSkillsDir}/my-skill");
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/SKILL.md", new MockFileData(ValidSkillMd("my-skill")));

        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        var result = await tool.ExecuteAsync("call-1", Args("patch", "my-skill",
            ("oldText", "DEFINITELY_NOT_PRESENT"),
            ("newText", "replacement")));

        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("not found");
    }

    [Fact]
    public async Task Patch_PathTraversalAttempt_ReturnsError()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory($"{WorkspaceSkillsDir}/my-skill");
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/SKILL.md", new MockFileData(ValidSkillMd("my-skill")));

        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        var result = await tool.ExecuteAsync("call-1", Args("patch", "my-skill",
            ("oldText", "foo"),
            ("newText", "bar"),
            ("filePath", "../other-skill/SKILL.md")));

        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("traversal");
    }

    [Fact]
    public async Task Patch_SupportingFile_WorksWithAllowedSubdir()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory($"{WorkspaceSkillsDir}/my-skill/references");
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/SKILL.md", new MockFileData(ValidSkillMd("my-skill")));
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/references/guide.md", new MockFileData("hello world"));

        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        var result = await tool.ExecuteAsync("call-1", Args("patch", "my-skill",
            ("oldText", "hello"),
            ("newText", "goodbye"),
            ("filePath", "references/guide.md")));

        ResultText(result).ShouldNotContain("Error:");
        fs.File.ReadAllText($"{WorkspaceSkillsDir}/my-skill/references/guide.md").ShouldContain("goodbye world");
    }

    // ── delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ExistingSkill_RemovesDirectory()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory($"{WorkspaceSkillsDir}/my-skill");
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/SKILL.md", new MockFileData(ValidSkillMd("my-skill")));

        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(deletionAllowed: true), fs);

        var result = await tool.ExecuteAsync("call-1", Args("delete", "my-skill"));
        ResultText(result).ShouldNotContain("Error:");
        fs.Directory.Exists($"{WorkspaceSkillsDir}/my-skill").ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_WhenDeletionDisabled_ReturnsError()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory($"{WorkspaceSkillsDir}/my-skill");
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/SKILL.md", new MockFileData(ValidSkillMd("my-skill")));

        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(deletionAllowed: false), fs);

        var result = await tool.ExecuteAsync("call-1", Args("delete", "my-skill"));
        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("AllowSkillDeletion");
        fs.Directory.Exists($"{WorkspaceSkillsDir}/my-skill").ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_NonExistentSkill_ReturnsError()
    {
        var fs = new MockFileSystem();
        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(deletionAllowed: true), fs);

        var result = await tool.ExecuteAsync("call-1", Args("delete", "ghost-skill"));
        ResultText(result).ShouldContain("Error:");
    }

    // ── write_file ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteFile_AllowedSubdir_WritesSuccessfully()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory($"{WorkspaceSkillsDir}/my-skill");
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/SKILL.md", new MockFileData(ValidSkillMd("my-skill")));

        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        var result = await tool.ExecuteAsync("call-1", Args("write_file", "my-skill",
            ("filePath", "references/cheatsheet.md"),
            ("content", "# Cheatsheet\nHello!")));

        ResultText(result).ShouldNotContain("Error:");
        fs.File.Exists($"{WorkspaceSkillsDir}/my-skill/references/cheatsheet.md").ShouldBeTrue();
    }

    [Fact]
    public async Task WriteFile_DisallowedSubdir_ReturnsError()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory($"{WorkspaceSkillsDir}/my-skill");
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/SKILL.md", new MockFileData(ValidSkillMd("my-skill")));

        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        var result = await tool.ExecuteAsync("call-1", Args("write_file", "my-skill",
            ("filePath", "secrets/passwords.txt"),
            ("content", "bad stuff")));

        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("references, templates, scripts, assets");
    }

    [Fact]
    public async Task WriteFile_PathTraversal_ReturnsError()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory($"{WorkspaceSkillsDir}/my-skill");
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/SKILL.md", new MockFileData(ValidSkillMd("my-skill")));

        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        var result = await tool.ExecuteAsync("call-1", Args("write_file", "my-skill",
            ("filePath", "../other-skill/evil.sh"),
            ("content", "rm -rf /")));

        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("traversal");
    }

    [Fact]
    public async Task WriteFile_SkillNotFound_ReturnsError()
    {
        var fs = new MockFileSystem();
        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        var result = await tool.ExecuteAsync("call-1", Args("write_file", "ghost-skill",
            ("filePath", "references/guide.md"),
            ("content", "something")));

        ResultText(result).ShouldContain("Error:");
    }

    // ── remove_file ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveFile_ExistingFile_DeletesIt()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory($"{WorkspaceSkillsDir}/my-skill/references");
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/SKILL.md", new MockFileData(ValidSkillMd("my-skill")));
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/references/guide.md", new MockFileData("content"));

        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(deletionAllowed: true), fs);

        var result = await tool.ExecuteAsync("call-1", Args("remove_file", "my-skill",
            ("filePath", "references/guide.md")));

        ResultText(result).ShouldNotContain("Error:");
        fs.File.Exists($"{WorkspaceSkillsDir}/my-skill/references/guide.md").ShouldBeFalse();
    }

    [Fact]
    public async Task RemoveFile_WhenDeletionDisabled_ReturnsError()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory($"{WorkspaceSkillsDir}/my-skill/references");
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/SKILL.md", new MockFileData(ValidSkillMd("my-skill")));
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/references/guide.md", new MockFileData("content"));

        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(deletionAllowed: false), fs);

        var result = await tool.ExecuteAsync("call-1", Args("remove_file", "my-skill",
            ("filePath", "references/guide.md")));

        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("AllowSkillDeletion");
    }

    [Fact]
    public async Task RemoveFile_SkillMd_ReturnsError()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory($"{WorkspaceSkillsDir}/my-skill");
        fs.AddFile($"{WorkspaceSkillsDir}/my-skill/SKILL.md", new MockFileData(ValidSkillMd("my-skill")));

        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(deletionAllowed: true), fs);

        var result = await tool.ExecuteAsync("call-1", Args("remove_file", "my-skill",
            ("filePath", "SKILL.md")));

        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("delete");
    }

    // ── source priority ───────────────────────────────────────────────────────

    [Fact]
    public async Task Edit_AgentScopedSkill_WorksWhenNoWorkspaceCopy()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory($"{AgentSkillsDir}/my-skill");
        fs.AddFile($"{AgentSkillsDir}/my-skill/SKILL.md", new MockFileData(ValidSkillMd("my-skill")));

        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        var result = await tool.ExecuteAsync("call-1", Args("edit", "my-skill",
            ("content", ValidSkillMd("my-skill") + "\nAgent update.")));

        ResultText(result).ShouldNotContain("Error:");
        fs.File.ReadAllText($"{AgentSkillsDir}/my-skill/SKILL.md").ShouldContain("Agent update.");
    }

    [Fact]
    public async Task Create_WorkspacePreferredOverAgent_WritestoWorkspace()
    {
        var fs = new MockFileSystem();
        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        await tool.ExecuteAsync("call-1", Args("create", "new-skill",
            ("content", ValidSkillMd("new-skill"))));

        fs.File.Exists($"{WorkspaceSkillsDir}/new-skill/SKILL.md").ShouldBeTrue();
        fs.File.Exists($"{AgentSkillsDir}/new-skill/SKILL.md").ShouldBeFalse();
    }

    // ── unknown action ────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownAction_ReturnsError()
    {
        var fs = new MockFileSystem();
        var tool = new SkillManagerTool(AgentSkillsDir, WorkspaceSkillsDir, EnabledConfig(), fs);

        var result = await tool.ExecuteAsync("call-1", Args("teleport", "my-skill"));
        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("Unknown action");
    }
}

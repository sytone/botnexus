using BotNexus.Extensions.Skills;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Extensions.Skills.Tests;

/// <summary>
/// Tests for PBI3 (#1830): linked-file progressive disclosure for loaded skills.
/// Covers linked-file discovery, the "Linked files" listing on load, and the
/// <c>view_file</c> action including single-file loading and traversal rejection.
/// </summary>
public sealed class SkillLinkedFilesTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "botnexus-linked-files-tests");

    private static string ResultText(BotNexus.Agent.Core.Types.AgentToolResult result)
        => string.Join("", result.Content.Select(c => c.Value));

    private static IReadOnlyDictionary<string, object?> Args(string action, string? skillName = null, string? filePath = null)
    {
        var dict = new Dictionary<string, object?> { ["action"] = action };
        if (skillName is not null) dict["skillName"] = skillName;
        if (filePath is not null) dict["filePath"] = filePath;
        return dict;
    }

    private static (MockFileSystem fs, string skillDir) CreateSkillWithFiles(string name)
    {
        var fs = new MockFileSystem();
        var skillDir = Path.Combine(Root, name);
        fs.Directory.CreateDirectory(skillDir);
        fs.File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), $"""
            ---
            name: {name}
            description: A skill with support files
            ---
            # {name}

            Body instructions.
            """);

        fs.Directory.CreateDirectory(Path.Combine(skillDir, "references"));
        fs.File.WriteAllText(Path.Combine(skillDir, "references", "api-reference.md"), "# API Reference\nDetailed docs.");
        fs.Directory.CreateDirectory(Path.Combine(skillDir, "scripts"));
        fs.File.WriteAllText(Path.Combine(skillDir, "scripts", "run.ps1"), "Write-Output 'hi'");

        return (fs, skillDir);
    }

    // ── discovery ────────────────────────────────────────────────

    [Fact]
    public void Discover_SkillWithSupportFiles_PopulatesLinkedFiles()
    {
        var (fs, _) = CreateSkillWithFiles("has-files");

        var skills = SkillDiscovery.Discover(Root, null, null, fs);

        var skill = skills.ShouldHaveSingleItem();
        skill.LinkedFiles.Count.ShouldBe(2);
        skill.LinkedFiles.ShouldContain(f => f.RelativePath == "references/api-reference.md" && f.Directory == "references");
        skill.LinkedFiles.ShouldContain(f => f.RelativePath == "scripts/run.ps1" && f.Directory == "scripts");
    }

    [Fact]
    public void Discover_SkillWithoutSupportFiles_HasEmptyLinkedFiles()
    {
        var fs = new MockFileSystem();
        var skillDir = Path.Combine(Root, "plain");
        fs.Directory.CreateDirectory(skillDir);
        fs.File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: plain\ndescription: no support files\n---\nBody.");

        var skills = SkillDiscovery.Discover(Root, null, null, fs);

        skills.ShouldHaveSingleItem().LinkedFiles.ShouldBeEmpty();
    }

    // ── load: linked-file listing ────────────────────────────────

    [Fact]
    public async Task Load_WithSupportFiles_ListsLinkedFilesGroupedByDirectory()
    {
        var (fs, _) = CreateSkillWithFiles("has-files");
        var tool = new SkillTool(SkillDiscovery.Discover(Root, null, null, fs), config: null, fs);

        var text = ResultText(await tool.ExecuteAsync("c1", Args("load", "has-files")));

        text.ShouldContain("Linked files");
        text.ShouldContain("references/");
        text.ShouldContain("references/api-reference.md");
        text.ShouldContain("scripts/");
        text.ShouldContain("scripts/run.ps1");
        // usage hint points at the view_file action
        text.ShouldContain("view_file");
    }

    [Fact]
    public async Task Load_WithoutSupportFiles_DoesNotShowLinkedFilesSection()
    {
        var fs = new MockFileSystem();
        var skillDir = Path.Combine(Root, "plain");
        fs.Directory.CreateDirectory(skillDir);
        fs.File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: plain\ndescription: no support files\n---\nBody.");
        var tool = new SkillTool(SkillDiscovery.Discover(Root, null, null, fs), config: null, fs);

        var text = ResultText(await tool.ExecuteAsync("c1", Args("load", "plain")));

        text.ShouldNotContain("Linked files");
    }

    // ── view_file: happy path ────────────────────────────────────

    [Fact]
    public async Task ViewFile_ReturnsSingleFileContent_WithoutFullSkillBody()
    {
        var (fs, _) = CreateSkillWithFiles("has-files");
        var tool = new SkillTool(SkillDiscovery.Discover(Root, null, null, fs), config: null, fs);

        var text = ResultText(await tool.ExecuteAsync("c1", Args("view_file", "has-files", "references/api-reference.md")));

        text.ShouldContain("Detailed docs.");
        text.ShouldContain("references/api-reference.md");
        // The skill's main body must NOT be injected by a single-file view.
        text.ShouldNotContain("Body instructions.");
    }

    [Fact]
    public async Task ViewFile_DoesNotMarkSkillLoaded()
    {
        var (fs, _) = CreateSkillWithFiles("has-files");
        var tool = new SkillTool(SkillDiscovery.Discover(Root, null, null, fs), config: null, fs);

        await tool.ExecuteAsync("c1", Args("view_file", "has-files", "scripts/run.ps1"));

        tool.SessionLoadedSkills.ShouldNotContain("has-files");
    }

    // ── view_file: sad paths ─────────────────────────────────────

    [Fact]
    public async Task ViewFile_TraversalPath_IsRejected()
    {
        var (fs, _) = CreateSkillWithFiles("has-files");
        // Create a secret file OUTSIDE the skill directory that traversal would target.
        fs.File.WriteAllText(Path.Combine(Root, "secret.txt"), "TOP SECRET");
        var tool = new SkillTool(SkillDiscovery.Discover(Root, null, null, fs), config: null, fs);

        var text = ResultText(await tool.ExecuteAsync("c1", Args("view_file", "has-files", "references/../../secret.txt")));

        text.ShouldContain("traversal");
        text.ShouldNotContain("TOP SECRET");
    }

    [Fact]
    public async Task ViewFile_PathOutsideAllowedDirs_IsRejected()
    {
        var (fs, _) = CreateSkillWithFiles("has-files");
        var tool = new SkillTool(SkillDiscovery.Discover(Root, null, null, fs), config: null, fs);

        var text = ResultText(await tool.ExecuteAsync("c1", Args("view_file", "has-files", "SKILL.md")));

        text.ShouldContain("must be under");
        text.ShouldNotContain("Body instructions.");
    }

    [Fact]
    public async Task ViewFile_NonexistentFile_ReturnsNotFound()
    {
        var (fs, _) = CreateSkillWithFiles("has-files");
        var tool = new SkillTool(SkillDiscovery.Discover(Root, null, null, fs), config: null, fs);

        var text = ResultText(await tool.ExecuteAsync("c1", Args("view_file", "has-files", "references/missing.md")));

        text.ShouldContain("not found");
    }

    [Fact]
    public async Task ViewFile_MissingSkillName_ReturnsError()
    {
        var (fs, _) = CreateSkillWithFiles("has-files");
        var tool = new SkillTool(SkillDiscovery.Discover(Root, null, null, fs), config: null, fs);

        var text = ResultText(await tool.ExecuteAsync("c1", Args("view_file", filePath: "references/api-reference.md")));

        text.ShouldContain("skillName is required");
    }

    [Fact]
    public async Task ViewFile_MissingFilePath_ReturnsError()
    {
        var (fs, _) = CreateSkillWithFiles("has-files");
        var tool = new SkillTool(SkillDiscovery.Discover(Root, null, null, fs), config: null, fs);

        var text = ResultText(await tool.ExecuteAsync("c1", Args("view_file", "has-files")));

        text.ShouldContain("filePath is required");
    }

    [Fact]
    public async Task ViewFile_UnknownSkill_ReturnsNotFound()
    {
        var fs = new MockFileSystem();
        var tool = new SkillTool([], config: null, fs);

        var text = ResultText(await tool.ExecuteAsync("c1", Args("view_file", "no-such-skill", "references/x.md")));

        text.ShouldContain("not found");
    }
}

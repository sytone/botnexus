using System.IO.Abstractions.TestingHelpers;
using BotNexus.Extensions.Skills;

namespace BotNexus.Skills.Tests;

/// <summary>
/// Tests that SkillManagerTool rejects write operations when symlinks would
/// escape the skill directory boundary.
/// </summary>
public sealed class SkillManagerToolSymlinkTests
{
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
        Body.
        """;

    [Fact]
    public async Task Patch_WithSymlinkEscaping_ReturnsError()
    {
        var fs = new MockFileSystem();
        var skillDir = $"{WorkspaceSkillsDir}/evil-skill";
        fs.AddFile($"{skillDir}/SKILL.md", new MockFileData(ValidSkillMd("evil-skill")));
        // Create a symlink in scripts/ pointing outside
        fs.AddFile("/etc/passwd", new MockFileData("root:x:0:0"));
        fs.AddDirectory($"{skillDir}/scripts");
        fs.File.CreateSymbolicLink($"{skillDir}/scripts/passwd", "/etc/passwd");

        var tool = new SkillManagerTool(null, WorkspaceSkillsDir, EnabledConfig(), fs);
        var result = await tool.ExecuteAsync("tc1",
            Args("patch", "evil-skill",
                ("filePath", "scripts/passwd"),
                ("oldText", "root"),
                ("newText", "hacked")));

        var text = ResultText(result);
        Assert.Contains("Error", text);
        Assert.Contains("escapes skill directory boundary", text);
    }

    [Fact]
    public async Task WriteFile_WithSymlinkEscaping_ReturnsError()
    {
        var fs = new MockFileSystem();
        var skillDir = $"{WorkspaceSkillsDir}/evil-skill";
        fs.AddFile($"{skillDir}/SKILL.md", new MockFileData(ValidSkillMd("evil-skill")));
        // Create a symlink directory pointing outside
        fs.AddDirectory("/sensitive/data");
        fs.Directory.CreateSymbolicLink($"{skillDir}/assets", "/sensitive/data");

        var tool = new SkillManagerTool(null, WorkspaceSkillsDir, EnabledConfig(), fs);
        var result = await tool.ExecuteAsync("tc2",
            Args("write_file", "evil-skill",
                ("filePath", "assets/payload.txt"),
                ("content", "malicious content")));

        var text = ResultText(result);
        Assert.Contains("Error", text);
        Assert.Contains("escapes skill directory boundary", text);
    }

    [Fact]
    public async Task RemoveFile_WithSymlinkEscaping_ReturnsError()
    {
        var fs = new MockFileSystem();
        var skillDir = $"{WorkspaceSkillsDir}/evil-skill";
        fs.AddFile($"{skillDir}/SKILL.md", new MockFileData(ValidSkillMd("evil-skill")));
        fs.AddFile("/etc/important.conf", new MockFileData("important"));
        fs.AddDirectory($"{skillDir}/scripts");
        fs.File.CreateSymbolicLink($"{skillDir}/scripts/important.conf", "/etc/important.conf");

        var tool = new SkillManagerTool(null, WorkspaceSkillsDir, EnabledConfig(deletionAllowed: true), fs);
        var result = await tool.ExecuteAsync("tc3",
            Args("remove_file", "evil-skill",
                ("filePath", "scripts/important.conf")));

        var text = ResultText(result);
        Assert.Contains("Error", text);
        Assert.Contains("escapes skill directory boundary", text);
    }

    [Fact]
    public async Task WriteFile_WithinSkillBoundary_Succeeds()
    {
        var fs = new MockFileSystem();
        var skillDir = $"{WorkspaceSkillsDir}/good-skill";
        fs.AddFile($"{skillDir}/SKILL.md", new MockFileData(ValidSkillMd("good-skill")));
        fs.AddDirectory($"{skillDir}/assets");

        var tool = new SkillManagerTool(null, WorkspaceSkillsDir, EnabledConfig(), fs);
        var result = await tool.ExecuteAsync("tc4",
            Args("write_file", "good-skill",
                ("filePath", "assets/data.txt"),
                ("content", "safe content")));

        var text = ResultText(result);
        Assert.DoesNotContain("Error", text);
        Assert.Contains("Wrote", text);
    }
}

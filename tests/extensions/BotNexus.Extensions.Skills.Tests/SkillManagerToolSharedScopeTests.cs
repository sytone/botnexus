using System.IO.Abstractions.TestingHelpers;
using BotNexus.Extensions.Skills;

namespace BotNexus.Skills.Tests;

/// <summary>
/// Tests for the shared (all-agent) skill management scope added in #1723.
/// Covers: the AllowSharedSkillManagement gate matrix, scope-argument routing to the
/// global/agent/workspace write roots, protection of global skills when the gate is off,
/// and that traversal/security still apply on the shared root.
/// </summary>
public sealed class SkillManagerToolSharedScopeTests
{
    private const string AgentSkillsDir = "/home/agent/.botnexus/agents/test-agent/skills";
    private const string WorkspaceSkillsDir = "/workspace/skills";
    private const string GlobalSkillsDir = "/home/agent/.botnexus/skills";

    private static SkillsConfig Config(bool sharedAllowed = false, bool deletionAllowed = false) => new()
    {
        AllowSkillCreation = true,
        AllowSkillDeletion = deletionAllowed,
        AllowSharedSkillManagement = sharedAllowed
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

    private static SkillManagerTool NewTool(MockFileSystem fs, SkillsConfig config) =>
        new(AgentSkillsDir, WorkspaceSkillsDir, GlobalSkillsDir, config, fs);

    // -- gate: shared writes blocked when off ---------------------------------

    [Fact]
    public async Task Create_SharedScope_GateOff_ReturnsErrorAndDoesNotWrite()
    {
        var fs = new MockFileSystem();
        var tool = NewTool(fs, Config(sharedAllowed: false));

        var result = await tool.ExecuteAsync("c1", Args("create", "team-skill",
            ("scope", "shared"),
            ("content", ValidSkillMd("team-skill"))));

        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("AllowSharedSkillManagement");
        fs.File.Exists($"{GlobalSkillsDir}/team-skill/SKILL.md").ShouldBeFalse();
    }

    // -- gate: shared writes allowed when on ----------------------------------

    [Fact]
    public async Task Create_SharedScope_GateOn_WritesToGlobal()
    {
        var fs = new MockFileSystem();
        var tool = NewTool(fs, Config(sharedAllowed: true));

        var result = await tool.ExecuteAsync("c1", Args("create", "team-skill",
            ("scope", "shared"),
            ("content", ValidSkillMd("team-skill"))));

        ResultText(result).ShouldNotContain("Error:");
        fs.File.Exists($"{GlobalSkillsDir}/team-skill/SKILL.md").ShouldBeTrue();
        fs.File.Exists($"{WorkspaceSkillsDir}/team-skill/SKILL.md").ShouldBeFalse();
    }

    // -- default scope unchanged: still workspace -----------------------------

    [Fact]
    public async Task Create_DefaultScope_GateOn_StillWritesWorkspace()
    {
        var fs = new MockFileSystem();
        var tool = NewTool(fs, Config(sharedAllowed: true));

        await tool.ExecuteAsync("c1", Args("create", "new-skill",
            ("content", ValidSkillMd("new-skill"))));

        fs.File.Exists($"{WorkspaceSkillsDir}/new-skill/SKILL.md").ShouldBeTrue();
        fs.File.Exists($"{GlobalSkillsDir}/new-skill/SKILL.md").ShouldBeFalse();
    }

    // -- agent scope routing --------------------------------------------------

    [Fact]
    public async Task Create_AgentScope_WritesToAgentDir()
    {
        var fs = new MockFileSystem();
        var tool = NewTool(fs, Config());

        await tool.ExecuteAsync("c1", Args("create", "agent-skill",
            ("scope", "agent"),
            ("content", ValidSkillMd("agent-skill"))));

        fs.File.Exists($"{AgentSkillsDir}/agent-skill/SKILL.md").ShouldBeTrue();
        fs.File.Exists($"{WorkspaceSkillsDir}/agent-skill/SKILL.md").ShouldBeFalse();
    }

    // -- global-only skill protected when gate off ----------------------------

    [Fact]
    public async Task Edit_GlobalSkill_GateOff_ReturnsError()
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{GlobalSkillsDir}/team-skill/SKILL.md", new MockFileData(ValidSkillMd("team-skill")));
        var tool = NewTool(fs, Config(sharedAllowed: false));

        var result = await tool.ExecuteAsync("e1", Args("edit", "team-skill",
            ("content", ValidSkillMd("team-skill") + "\nedited")));

        ResultText(result).ShouldContain("Error:");
        fs.File.ReadAllText($"{GlobalSkillsDir}/team-skill/SKILL.md").ShouldNotContain("edited");
    }

    [Fact]
    public async Task Edit_GlobalSkill_GateOn_Succeeds()
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{GlobalSkillsDir}/team-skill/SKILL.md", new MockFileData(ValidSkillMd("team-skill")));
        var tool = NewTool(fs, Config(sharedAllowed: true));

        var result = await tool.ExecuteAsync("e1", Args("edit", "team-skill",
            ("content", ValidSkillMd("team-skill") + "\nedited")));

        ResultText(result).ShouldNotContain("Error:");
        fs.File.ReadAllText($"{GlobalSkillsDir}/team-skill/SKILL.md").ShouldContain("edited");
    }

    // -- delete on shared still needs AllowSkillDeletion too ------------------

    [Fact]
    public async Task Delete_GlobalSkill_GateOnButDeletionOff_ReturnsError()
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{GlobalSkillsDir}/team-skill/SKILL.md", new MockFileData(ValidSkillMd("team-skill")));
        var tool = NewTool(fs, Config(sharedAllowed: true, deletionAllowed: false));

        var result = await tool.ExecuteAsync("d1", Args("delete", "team-skill"));

        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("AllowSkillDeletion");
        fs.Directory.Exists($"{GlobalSkillsDir}/team-skill").ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_GlobalSkill_BothGatesOn_Succeeds()
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{GlobalSkillsDir}/team-skill/SKILL.md", new MockFileData(ValidSkillMd("team-skill")));
        var tool = NewTool(fs, Config(sharedAllowed: true, deletionAllowed: true));

        var result = await tool.ExecuteAsync("d1", Args("delete", "team-skill"));

        ResultText(result).ShouldNotContain("Error:");
        fs.Directory.Exists($"{GlobalSkillsDir}/team-skill").ShouldBeFalse();
    }

    // -- traversal still enforced on shared root ------------------------------

    [Fact]
    public async Task WriteFile_SharedSkill_PathTraversal_ReturnsError()
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{GlobalSkillsDir}/team-skill/SKILL.md", new MockFileData(ValidSkillMd("team-skill")));
        var tool = NewTool(fs, Config(sharedAllowed: true));

        var result = await tool.ExecuteAsync("w1", Args("write_file", "team-skill",
            ("filePath", "../other-skill/evil.sh"),
            ("content", "rm -rf /")));

        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("traversal");
    }

    // -- invalid scope rejected -----------------------------------------------

    [Fact]
    public async Task Create_InvalidScope_ReturnsError()
    {
        var fs = new MockFileSystem();
        var tool = NewTool(fs, Config(sharedAllowed: true));

        var result = await tool.ExecuteAsync("c1", Args("create", "x-skill",
            ("scope", "everywhere"),
            ("content", ValidSkillMd("x-skill"))));

        ResultText(result).ShouldContain("Error:");
        ResultText(result).ShouldContain("scope");
    }
}

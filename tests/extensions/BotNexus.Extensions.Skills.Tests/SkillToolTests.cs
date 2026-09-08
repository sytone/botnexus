using BotNexus.Extensions.Skills;
using BotNexus.Gateway.Abstractions.Models;
using System.Reflection;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillToolTests
{
    private static SkillDefinition MakeSkill(
        string name,
        string? description = null,
        string? content = null,
        SkillSource source = SkillSource.Global,
        string? sourcePath = null)
        => new()
        {
            Name = name,
            Description = description ?? $"{name} skill description",
            Content = content ?? $"Content for {name}",
            Source = source,
            SourcePath = sourcePath ?? $"/skills/{name}"
        };

    private static IReadOnlyDictionary<string, object?> Args(string action, string? skillName = null)
    {
        var dict = new Dictionary<string, object?> { ["action"] = action };
        if (skillName is not null)
            dict["skillName"] = skillName;
        return dict;
    }

    private static string ResultText(BotNexus.Agent.Core.Types.AgentToolResult result)
        => string.Join("", result.Content.Select(c => c.Value));

    // ──────────────────────────── list ────────────────────────────

    [Fact]
    public async Task List_ReturnsLoadedAndAvailableSkills()
    {
        var skills = new[] { MakeSkill("email-triage"), MakeSkill("calendar") };
        var config = new SkillsConfig { AutoLoad = ["email-triage"] };
        var tool = new SkillTool(skills, config);

        var result = await tool.ExecuteAsync("call-1", Args("list"));
        var text = ResultText(result);

        text.ShouldContain("email-triage");
        text.ShouldContain("calendar");
        text.ShouldContain("Loaded Skills");
        text.ShouldContain("Available Skills");
    }

    [Fact]
    public async Task List_WithAutoLoad_ShowsCorrectSplit()
    {
        var skills = new[] { MakeSkill("git-workflow"), MakeSkill("docs"), MakeSkill("testing") };
        var config = new SkillsConfig { AutoLoad = ["git-workflow", "testing"] };
        var tool = new SkillTool(skills, config);

        var result = await tool.ExecuteAsync("call-1", Args("list"));
        var text = ResultText(result);

        // AutoLoaded skills appear under "Loaded"
        text.ShouldContain("Loaded Skills");
        text.ShouldContain("git-workflow");
        text.ShouldContain("testing");

        // Non-autoloaded skill appears under "Available"
        text.ShouldContain("Available Skills");
        text.ShouldContain("docs");
    }

    [Fact]
    public async Task List_WithDenyList_DeniedSkillsNotShown()
    {
        var skills = new[] { MakeSkill("public-skill"), MakeSkill("secret-skill") };
        var config = new SkillsConfig { Disabled = ["secret-skill"] };
        var tool = new SkillTool(skills, config);

        var result = await tool.ExecuteAsync("call-1", Args("list"));
        var text = ResultText(result);

        text.ShouldContain("public-skill");
        text.ShouldNotContain("secret-skill");
    }

    [Fact]
    public async Task List_WithAllowList_OnlyAllowedSkillsShown()
    {
        var skills = new[] { MakeSkill("alpha"), MakeSkill("beta"), MakeSkill("gamma") };
        var config = new SkillsConfig { Allowed = ["alpha", "gamma"] };
        var tool = new SkillTool(skills, config);

        var result = await tool.ExecuteAsync("call-1", Args("list"));
        var text = ResultText(result);

        text.ShouldContain("alpha");
        text.ShouldContain("gamma");
        text.ShouldNotContain("beta");
    }

    [Fact]
    public async Task List_WithDisabledConfig_ReturnsNothing()
    {
        var skills = new[] { MakeSkill("email-triage") };
        var config = new SkillsConfig { Enabled = false };
        var tool = new SkillTool(skills, config);

        var result = await tool.ExecuteAsync("call-1", Args("list"));
        var text = ResultText(result);

        text.ShouldContain("No skills available.");
    }

    // ──────────────────────────── load ────────────────────────────

    [Fact]
    public async Task Load_ReturnsSkillContent()
    {
        var skills = new[] { MakeSkill("git-workflow", content: "Use feature branches.") };
        var tool = new SkillTool(skills, config: null);

        var result = await tool.ExecuteAsync("call-1", Args("load", "git-workflow"));
        var text = ResultText(result);

        text.ShouldContain("git-workflow");
        text.ShouldContain("Use feature branches.");
    }

    [Fact]
    public async Task Load_DeniedSkill_ReturnsError()
    {
        var skills = new[] { MakeSkill("forbidden") };
        var config = new SkillsConfig { Disabled = ["forbidden"] };
        var tool = new SkillTool(skills, config);

        var result = await tool.ExecuteAsync("call-1", Args("load", "forbidden"));
        var text = ResultText(result);

        text.ShouldContain("not available");
    }

    [Fact]
    public async Task Load_NonexistentSkill_ReturnsError()
    {
        var tool = new SkillTool([], config: null);

        var result = await tool.ExecuteAsync("call-1", Args("load", "no-such-skill"));
        var text = ResultText(result);

        text.ShouldContain("not found");
    }

    [Fact]
    public async Task Load_MissingSkillName_ReturnsError()
    {
        var tool = new SkillTool([], config: null);

        var result = await tool.ExecuteAsync("call-1", Args("load"));
        var text = ResultText(result);

        text.ShouldContain("skillName is required");
    }

    [Fact]
    public async Task Load_SkillAppearsInSubsequentList()
    {
        var skills = new[] { MakeSkill("calendar"), MakeSkill("email") };
        var tool = new SkillTool(skills, config: null);

        // Initially nothing loaded
        var listBefore = ResultText(await tool.ExecuteAsync("c1", Args("list")));
        listBefore.ShouldNotContain("Loaded Skills");

        // Load one skill
        await tool.ExecuteAsync("c2", Args("load", "calendar"));

        // Now it should appear as loaded
        var listAfter = ResultText(await tool.ExecuteAsync("c3", Args("list")));
        listAfter.ShouldContain("Loaded Skills");
        listAfter.ShouldContain("calendar");
    }

    [Fact]
    public async Task Load_MultiplLoadsAccumulate()
    {
        var skills = new[] { MakeSkill("a"), MakeSkill("b"), MakeSkill("c") };
        var tool = new SkillTool(skills, config: null);

        await tool.ExecuteAsync("c1", Args("load", "a"));
        await tool.ExecuteAsync("c2", Args("load", "b"));

        tool.SessionLoadedSkills.ShouldContain("a");
        tool.SessionLoadedSkills.ShouldContain("b");
        tool.SessionLoadedSkills.ShouldNotContain("c");

        var text = ResultText(await tool.ExecuteAsync("c3", Args("list")));
        text.ShouldContain("Loaded Skills");
        text.ShouldContain("a");
        text.ShouldContain("b");
        text.ShouldContain("Available Skills");
        text.ShouldContain("c");
    }

    [Fact]
    public async Task TryUnload_LoadedSkill_ReturnsTrue()
    {
        var tool = new SkillTool([MakeSkill("calendar")], config: null);
        await tool.ExecuteAsync("c1", Args("load", "calendar"));

        var result = InvokeTryUnload(tool, "calendar");

        result.ShouldBeTrue();
    }

    [Fact]
    public void TryUnload_NotLoaded_ReturnsFalse()
    {
        var tool = new SkillTool([MakeSkill("calendar")], config: null);

        var result = InvokeTryUnload(tool, "calendar");

        result.ShouldBeFalse();
    }

    [Fact]
    public void DiscoveryPaths_ReturnsConfiguredPaths()
    {
        var tool = new SkillTool("global-path", "agent-path", "workspace-path", config: null);

        var result = InvokeDiscoveryPaths(tool);

        result.ShouldBe(("global-path", "agent-path", "workspace-path"));
    }

    [Fact]
    public void Config_ReturnsSkillsConfig()
    {
        var config = new SkillsConfig { Enabled = true };
        var tool = new SkillTool("global-path", "agent-path", "workspace-path", config);

        var result = InvokeConfig(tool);

        result.ShouldBeSameAs(config);
    }

    // ── #227 path resolution ──────────────────────────────────────────────

    [Fact]
    public async Task List_AvailableSkills_IncludePath()
    {
        var skill = MakeSkill("calendar");
        var tool = new SkillTool([skill], config: null);

        var result = await tool.ExecuteAsync("call-1", Args("list"));
        var text = ResultText(result);

        text.ShouldContain("Path:");
        text.ShouldContain("/skills/calendar");
    }

    [Fact]
    public async Task List_LoadedSkills_IncludePath()
    {
        var skills = new[] { MakeSkill("email-triage") };
        var config = new SkillsConfig { AutoLoad = ["email-triage"] };
        var tool = new SkillTool(skills, config);

        var result = await tool.ExecuteAsync("call-1", Args("list"));
        var text = ResultText(result);

        text.ShouldContain("Path:");
        text.ShouldContain("/skills/email-triage");
    }

    [Fact]
    public async Task Load_ResponseIncludesPath()
    {
        var skill = MakeSkill("git-workflow");
        var tool = new SkillTool([skill], config: null);

        var result = await tool.ExecuteAsync("call-1", Args("load", "git-workflow"));
        var text = ResultText(result);

        text.ShouldContain("**Path:**");
        text.ShouldContain("/skills/git-workflow");
    }

    // #3712: there is more than one skill root, so the resolved root must be named at point of
    // use. An agent that only sees a bare path has no way to know which of the four tiers it
    // came from, and hard-codes the shared root for an agent-local skill.
    [Fact]
    public async Task Load_NamesResolvedRootTier()
    {
        var skill = MakeSkill(
            "botnexus-maintenance",
            source: SkillSource.Workspace,
            sourcePath: "/agents/farnsworth/workspace/skills/botnexus-maintenance");
        var tool = new SkillTool([skill], config: null);

        var result = await tool.ExecuteAsync("call-1", Args("load", "botnexus-maintenance"));
        var text = ResultText(result);

        text.ShouldContain("**Resolved from:** Workspace skill root");
        text.ShouldContain("/agents/farnsworth/workspace/skills/botnexus-maintenance");
    }

    [Theory]
    [InlineData(SkillSource.Plugin, "Plugin")]
    [InlineData(SkillSource.Global, "Global")]
    [InlineData(SkillSource.Agent, "Agent")]
    [InlineData(SkillSource.Workspace, "Workspace")]
    public async Task Load_NamesEveryRootTier(SkillSource source, string expectedTier)
    {
        var tool = new SkillTool([MakeSkill("tiered", source: source)], config: null);

        var result = await tool.ExecuteAsync("call-1", Args("load", "tiered"));

        ResultText(result).ShouldContain($"**Resolved from:** {expectedTier} skill root");
    }

    // The whole point of #3712: the load output must steer the agent to build support-file
    // paths from the resolved directory instead of a hard-coded absolute root.
    [Fact]
    public async Task Load_TellsAgentToBuildPathsFromResolvedDirectory()
    {
        var tool = new SkillTool([MakeSkill("git-workflow")], config: null);

        var result = await tool.ExecuteAsync("call-1", Args("load", "git-workflow"));

        ResultText(result).ShouldContain("Resolve scripts and support files against this directory");
    }

    [Fact]
    public async Task Load_UnknownSkill_DoesNotIncludePath()
    {
        var tool = new SkillTool([], config: null);

        var result = await tool.ExecuteAsync("call-1", Args("load", "nonexistent"));
        var text = ResultText(result);

        text.ShouldContain("not found");
        text.ShouldNotContain("**Path:**");
    }

    private static bool InvokeTryUnload(SkillTool tool, string skillName)
    {
        var method = tool.GetType().GetMethod("TryUnload", BindingFlags.Public | BindingFlags.Instance);
        method.ShouldNotBeNull("SkillTool must expose TryUnload for /skills remove.");
        return (bool)method!.Invoke(tool, [skillName])!;
    }

    private static (string? Global, string? Agent, string? Workspace) InvokeDiscoveryPaths(SkillTool tool)
    {
        var property = tool.GetType().GetProperty("DiscoveryPaths", BindingFlags.Public | BindingFlags.Instance);
        property.ShouldNotBeNull("SkillTool must expose DiscoveryPaths for /skills command output.");
        return ((string? Global, string? Agent, string? Workspace))property!.GetValue(tool)!;
    }

    private static SkillsConfig? InvokeConfig(SkillTool tool)
    {
        var property = tool.GetType().GetProperty("Config", BindingFlags.Public | BindingFlags.Instance);
        property.ShouldNotBeNull("SkillTool must expose Config for /skills command output.");
        return (SkillsConfig?)property!.GetValue(tool);
    }
}

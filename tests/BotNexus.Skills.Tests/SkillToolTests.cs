using BotNexus.Extensions.Skills;
using BotNexus.Gateway.Abstractions.Models;
using FluentAssertions;
using System.Reflection;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillToolTests
{
    private static SkillDefinition MakeSkill(string name, string? description = null, string? content = null)
        => new()
        {
            Name = name,
            Description = description ?? $"{name} skill description",
            Content = content ?? $"Content for {name}",
            Source = SkillSource.Global,
            SourcePath = $"/skills/{name}"
        };

    private static IReadOnlyDictionary<string, object?> Args(string action, string? skillName = null)
    {
        var dict = new Dictionary<string, object?> { ["action"] = action };
        if (skillName is not null)
            dict["skillName"] = skillName;
        return dict;
    }

    private static string ResultText(AgentCore.Types.AgentToolResult result)
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

        text.Should().Contain("email-triage");
        text.Should().Contain("calendar");
        text.Should().Contain("Loaded Skills");
        text.Should().Contain("Available Skills");
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
        text.Should().Contain("Loaded Skills");
        text.Should().Contain("git-workflow");
        text.Should().Contain("testing");

        // Non-autoloaded skill appears under "Available"
        text.Should().Contain("Available Skills");
        text.Should().Contain("docs");
    }

    [Fact]
    public async Task List_WithDenyList_DeniedSkillsNotShown()
    {
        var skills = new[] { MakeSkill("public-skill"), MakeSkill("secret-skill") };
        var config = new SkillsConfig { Disabled = ["secret-skill"] };
        var tool = new SkillTool(skills, config);

        var result = await tool.ExecuteAsync("call-1", Args("list"));
        var text = ResultText(result);

        text.Should().Contain("public-skill");
        text.Should().NotContain("secret-skill");
    }

    [Fact]
    public async Task List_WithAllowList_OnlyAllowedSkillsShown()
    {
        var skills = new[] { MakeSkill("alpha"), MakeSkill("beta"), MakeSkill("gamma") };
        var config = new SkillsConfig { Allowed = ["alpha", "gamma"] };
        var tool = new SkillTool(skills, config);

        var result = await tool.ExecuteAsync("call-1", Args("list"));
        var text = ResultText(result);

        text.Should().Contain("alpha");
        text.Should().Contain("gamma");
        text.Should().NotContain("beta");
    }

    [Fact]
    public async Task List_WithDisabledConfig_ReturnsNothing()
    {
        var skills = new[] { MakeSkill("email-triage") };
        var config = new SkillsConfig { Enabled = false };
        var tool = new SkillTool(skills, config);

        var result = await tool.ExecuteAsync("call-1", Args("list"));
        var text = ResultText(result);

        text.Should().Contain("No skills available.");
    }

    // ──────────────────────────── load ────────────────────────────

    [Fact]
    public async Task Load_ReturnsSkillContent()
    {
        var skills = new[] { MakeSkill("git-workflow", content: "Use feature branches.") };
        var tool = new SkillTool(skills, config: null);

        var result = await tool.ExecuteAsync("call-1", Args("load", "git-workflow"));
        var text = ResultText(result);

        text.Should().Contain("git-workflow");
        text.Should().Contain("Use feature branches.");
    }

    [Fact]
    public async Task Load_DeniedSkill_ReturnsError()
    {
        var skills = new[] { MakeSkill("forbidden") };
        var config = new SkillsConfig { Disabled = ["forbidden"] };
        var tool = new SkillTool(skills, config);

        var result = await tool.ExecuteAsync("call-1", Args("load", "forbidden"));
        var text = ResultText(result);

        text.Should().Contain("not available");
    }

    [Fact]
    public async Task Load_NonexistentSkill_ReturnsError()
    {
        var tool = new SkillTool([], config: null);

        var result = await tool.ExecuteAsync("call-1", Args("load", "no-such-skill"));
        var text = ResultText(result);

        text.Should().Contain("not found");
    }

    [Fact]
    public async Task Load_MissingSkillName_ReturnsError()
    {
        var tool = new SkillTool([], config: null);

        var result = await tool.ExecuteAsync("call-1", Args("load"));
        var text = ResultText(result);

        text.Should().Contain("skillName is required");
    }

    [Fact]
    public async Task Load_SkillAppearsInSubsequentList()
    {
        var skills = new[] { MakeSkill("calendar"), MakeSkill("email") };
        var tool = new SkillTool(skills, config: null);

        // Initially nothing loaded
        var listBefore = ResultText(await tool.ExecuteAsync("c1", Args("list")));
        listBefore.Should().NotContain("Loaded Skills");

        // Load one skill
        await tool.ExecuteAsync("c2", Args("load", "calendar"));

        // Now it should appear as loaded
        var listAfter = ResultText(await tool.ExecuteAsync("c3", Args("list")));
        listAfter.Should().Contain("Loaded Skills");
        listAfter.Should().Contain("calendar");
    }

    [Fact]
    public async Task Load_MultiplLoadsAccumulate()
    {
        var skills = new[] { MakeSkill("a"), MakeSkill("b"), MakeSkill("c") };
        var tool = new SkillTool(skills, config: null);

        await tool.ExecuteAsync("c1", Args("load", "a"));
        await tool.ExecuteAsync("c2", Args("load", "b"));

        tool.SessionLoadedSkills.Should().Contain("a");
        tool.SessionLoadedSkills.Should().Contain("b");
        tool.SessionLoadedSkills.Should().NotContain("c");

        var text = ResultText(await tool.ExecuteAsync("c3", Args("list")));
        text.Should().Contain("Loaded Skills");
        text.Should().Contain("a");
        text.Should().Contain("b");
        text.Should().Contain("Available Skills");
        text.Should().Contain("c");
    }

    [Fact]
    public async Task TryUnload_LoadedSkill_ReturnsTrue()
    {
        var tool = new SkillTool([MakeSkill("calendar")], config: null);
        await tool.ExecuteAsync("c1", Args("load", "calendar"));

        var result = InvokeTryUnload(tool, "calendar");

        result.Should().BeTrue();
    }

    [Fact]
    public void TryUnload_NotLoaded_ReturnsFalse()
    {
        var tool = new SkillTool([MakeSkill("calendar")], config: null);

        var result = InvokeTryUnload(tool, "calendar");

        result.Should().BeFalse();
    }

    [Fact]
    public void DiscoveryPaths_ReturnsConfiguredPaths()
    {
        var tool = new SkillTool("global-path", "agent-path", "workspace-path", config: null);

        var result = InvokeDiscoveryPaths(tool);

        result.Should().Be(("global-path", "agent-path", "workspace-path"));
    }

    [Fact]
    public void Config_ReturnsSkillsConfig()
    {
        var config = new SkillsConfig { Enabled = true };
        var tool = new SkillTool("global-path", "agent-path", "workspace-path", config);

        var result = InvokeConfig(tool);

        result.Should().BeSameAs(config);
    }

    private static bool InvokeTryUnload(SkillTool tool, string skillName)
    {
        var method = tool.GetType().GetMethod("TryUnload", BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull("SkillTool must expose TryUnload for /skills remove.");
        return (bool)method!.Invoke(tool, [skillName])!;
    }

    private static (string? Global, string? Agent, string? Workspace) InvokeDiscoveryPaths(SkillTool tool)
    {
        var property = tool.GetType().GetProperty("DiscoveryPaths", BindingFlags.Public | BindingFlags.Instance);
        property.Should().NotBeNull("SkillTool must expose DiscoveryPaths for /skills command output.");
        return ((string? Global, string? Agent, string? Workspace))property!.GetValue(tool)!;
    }

    private static SkillsConfig? InvokeConfig(SkillTool tool)
    {
        var property = tool.GetType().GetProperty("Config", BindingFlags.Public | BindingFlags.Instance);
        property.Should().NotBeNull("SkillTool must expose Config for /skills command output.");
        return (SkillsConfig?)property!.GetValue(tool);
    }
}

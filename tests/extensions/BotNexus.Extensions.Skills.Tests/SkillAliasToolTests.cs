using BotNexus.Extensions.Skills;
using System.Text.Json;

namespace BotNexus.Extensions.Skills.Tests;

/// <summary>
/// Tests for the explicit skill tool aliases (skills_list / skill_view) added for model
/// ergonomics (#1831). These lightweight tools delegate to the same <see cref="SkillTool"/>
/// implementation and shared session state, giving models distinct, clearly-named tools
/// while the multi-action <c>skills</c> tool remains fully backward-compatible.
/// </summary>
public sealed class SkillAliasToolTests
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

    private static string ResultText(BotNexus.Agent.Core.Types.AgentToolResult result)
        => string.Join("", result.Content.Select(c => c.Value));

    // ──────────────────────────── skills_list ────────────────────────────

    [Fact]
    public void SkillsListAlias_HasExplicitName()
    {
        var inner = new SkillTool([MakeSkill("calendar")], config: null);
        var alias = SkillAliasTool.CreateListAlias(inner);

        alias.Name.ShouldBe("skills_list");
        alias.Definition.Name.ShouldBe("skills_list");
    }

    [Fact]
    public void SkillsListAlias_HasDescriptiveDefinition()
    {
        var inner = new SkillTool([MakeSkill("calendar")], config: null);
        var alias = SkillAliasTool.CreateListAlias(inner);

        // The description must clearly explain what the tool does, and cross-reference the skills tool.
        alias.Definition.Description.ShouldNotBeNullOrWhiteSpace();
        alias.Definition.Description.ToLowerInvariant().ShouldContain("list");
        alias.Definition.Description.ShouldContain("skills");
    }

    [Fact]
    public async Task SkillsListAlias_ListsSkills_WithoutActionArgument()
    {
        var skills = new[] { MakeSkill("email-triage"), MakeSkill("calendar") };
        var config = new SkillsConfig { AutoLoad = ["email-triage"] };
        var inner = new SkillTool(skills, config);
        var alias = SkillAliasTool.CreateListAlias(inner);

        // No 'action' argument supplied - the alias forces the list action.
        var result = await alias.ExecuteAsync("call-1", new Dictionary<string, object?>());
        var text = ResultText(result);

        text.ShouldContain("email-triage");
        text.ShouldContain("calendar");
        text.ShouldContain("Loaded Skills");
        text.ShouldContain("Available Skills");
    }

    [Fact]
    public async Task SkillsListAlias_ReflectsSharedSessionState()
    {
        var skills = new[] { MakeSkill("calendar"), MakeSkill("email") };
        var inner = new SkillTool(skills, config: null);
        var alias = SkillAliasTool.CreateListAlias(inner);

        // Load through the underlying tool; the alias must observe the same session state.
        await inner.ExecuteAsync("c1", new Dictionary<string, object?> { ["action"] = "load", ["skillName"] = "calendar" });

        var text = ResultText(await alias.ExecuteAsync("c2", new Dictionary<string, object?>()));
        text.ShouldContain("Loaded Skills");
        text.ShouldContain("calendar");
    }

    // ──────────────────────────── skill_view ────────────────────────────

    [Fact]
    public void SkillViewAlias_HasExplicitName()
    {
        var inner = new SkillTool([MakeSkill("calendar")], config: null);
        var alias = SkillAliasTool.CreateViewAlias(inner);

        alias.Name.ShouldBe("skill_view");
        alias.Definition.Name.ShouldBe("skill_view");
    }

    [Fact]
    public void SkillViewAlias_DefinitionExposesSkillNameAndFilePath()
    {
        var inner = new SkillTool([MakeSkill("calendar")], config: null);
        var alias = SkillAliasTool.CreateViewAlias(inner);

        var schema = alias.Definition.Parameters;
        var props = schema.GetProperty("properties");
        props.TryGetProperty("skillName", out _).ShouldBeTrue();
        props.TryGetProperty("filePath", out _).ShouldBeTrue();

        // 'action' must NOT be a caller-visible parameter on the alias - it is implied.
        props.TryGetProperty("action", out _).ShouldBeFalse();

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        required.ShouldContain("skillName");
        required.ShouldContain("filePath");
    }

    [Fact]
    public async Task SkillViewAlias_MissingSkillName_ReturnsError()
    {
        var inner = new SkillTool([MakeSkill("calendar")], config: null);
        var alias = SkillAliasTool.CreateViewAlias(inner);

        var result = await alias.ExecuteAsync("call-1", new Dictionary<string, object?> { ["filePath"] = "references/x.md" });
        var text = ResultText(result);

        text.ShouldContain("skillName is required");
    }

    [Fact]
    public async Task SkillViewAlias_MissingFilePath_ReturnsError()
    {
        var inner = new SkillTool([MakeSkill("calendar")], config: null);
        var alias = SkillAliasTool.CreateViewAlias(inner);

        var result = await alias.ExecuteAsync("call-1", new Dictionary<string, object?> { ["skillName"] = "calendar" });
        var text = ResultText(result);

        text.ShouldContain("filePath is required");
    }

    [Fact]
    public async Task SkillViewAlias_RejectsPathTraversal()
    {
        var inner = new SkillTool([MakeSkill("calendar")], config: null);
        var alias = SkillAliasTool.CreateViewAlias(inner);

        var result = await alias.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["skillName"] = "calendar",
            ["filePath"] = "../secret.md"
        });
        var text = ResultText(result);

        text.ShouldContain("traversal");
    }

    // ──────────────────────────── backward-compat contract ────────────────────────────

    [Fact]
    public async Task SkillsTool_RetainsListLoadViewFileActions()
    {
        // The multi-action skills tool must keep its existing contract intact.
        var inner = new SkillTool([MakeSkill("calendar")], config: null);
        inner.Name.ShouldBe("skills");

        var enumValues = inner.Definition.Parameters
            .GetProperty("properties")
            .GetProperty("action")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        enumValues.ShouldContain("list");
        enumValues.ShouldContain("load");
        enumValues.ShouldContain("view_file");

        // list action still works with explicit action arg.
        var text = ResultText(await inner.ExecuteAsync("c1", new Dictionary<string, object?> { ["action"] = "list" }));
        text.ShouldContain("calendar");
    }
}

using System.Text.RegularExpressions;
using BotNexus.Extensions.Skills;

namespace BotNexus.Skills.Tests;

/// <summary>
/// Anti-drift tests for the <c>scope</c> default of <see cref="SkillManagerTool"/> (#2539).
/// The schema description, the XML docs, and the resolved value drifted apart once: the
/// description simultaneously annotated 'agent' with "(default)" and closed with
/// "Default is workspace." while the code resolved to <see cref="SkillSource.Workspace"/>.
/// These tests pin the resolved value and assert the model-facing description states the
/// default exactly once, so the contradiction cannot silently return.
/// </summary>
public sealed class SkillManagerToolScopeDefaultTests
{
    private static SkillsConfig Config() => new() { AllowSkillCreation = true };

    private static SkillManagerTool NewTool() =>
        new("/agent/skills", "/workspace/skills", "/global/skills", Config());

    /// <summary>Reads the model-facing description string for the <c>scope</c> property.</summary>
    private static string ScopeDescription()
    {
        var parameters = NewTool().Definition.Parameters;
        return parameters
            .GetProperty("properties")
            .GetProperty("scope")
            .GetProperty("description")
            .GetString()
            .ShouldNotBeNull();
    }

    // -- the resolved default: assert the VALUE, not the prose --------------------

    [Fact]
    public void TryResolveScope_ScopeOmitted_ResolvesToWorkspace()
    {
        var args = new Dictionary<string, object?> { ["action"] = "create", ["name"] = "x" };

        var ok = SkillManagerTool.TryResolveScope(args, out var scope, out var error);

        ok.ShouldBeTrue(error);
        scope.ShouldBe(SkillSource.Workspace);
    }

    [Fact]
    public void TryResolveScope_ScopeBlank_ResolvesToWorkspace()
    {
        var args = new Dictionary<string, object?> { ["scope"] = "   " };

        var ok = SkillManagerTool.TryResolveScope(args, out var scope, out var error);

        ok.ShouldBeTrue(error);
        scope.ShouldBe(SkillSource.Workspace);
    }

    // -- the description must state the default exactly once ---------------------

    [Fact]
    public void ScopeDescription_MentionsDefaultExactlyOnce()
    {
        var description = ScopeDescription();

        var matches = Regex.Matches(description, "default", RegexOptions.IgnoreCase);

        matches.Count.ShouldBe(
            1,
            $"The scope description must state the default exactly once, but said 'default' {matches.Count} time(s). Description: {description}");
    }

    [Fact]
    public void ScopeDescription_SentenceNamingTheDefault_NamesWorkspace()
    {
        var description = ScopeDescription();

        var sentence = description
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(s => s.Contains("default", StringComparison.OrdinalIgnoreCase));

        sentence.ShouldContain("workspace", Case.Insensitive);
        sentence.ShouldNotContain("agent", Case.Insensitive);
    }
}

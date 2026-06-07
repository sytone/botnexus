using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Prompts.Tests;

public sealed class ShellEfficiencySectionTests
{
    private static PromptContext DefaultContext => new() { WorkspaceDir = "C:/workspace" };

    [Fact]
    public void SectionId_IsShellEfficiency()
    {
        ShellEfficiencySection.Id.ShouldBe("shell-efficiency");
    }

    [Fact]
    public void SectionOrder_Is35()
    {
        ShellEfficiencySection.SectionOrder.ShouldBe(35);
    }

    [Fact]
    public void Create_ReturnsLambdaPromptSection()
    {
        var section = ShellEfficiencySection.Create();

        section.ShouldNotBeNull();
        section.ShouldBeOfType<LambdaPromptSection>();
    }

    [Fact]
    public void Create_SectionHasCorrectOrder()
    {
        var section = ShellEfficiencySection.Create();

        section.Order.ShouldBe(35);
    }

    [Fact]
    public void Create_SectionHasCorrectId()
    {
        var section = ShellEfficiencySection.Create();

        section.SectionId.ShouldBe("shell-efficiency");
    }

    [Fact]
    public void ShouldInclude_AlwaysReturnsTrue()
    {
        var section = ShellEfficiencySection.Create();

        section.ShouldInclude(DefaultContext).ShouldBeTrue();
    }

    [Fact]
    public void Build_ReturnsNonEmptyLines()
    {
        var section = ShellEfficiencySection.Create();

        var lines = section.Build(DefaultContext);

        lines.ShouldNotBeEmpty();
        lines.Count.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void Build_StartsWithMarkdownHeader()
    {
        var section = ShellEfficiencySection.Create();

        var lines = section.Build(DefaultContext);

        lines[0].ShouldBe("## Shell Efficiency");
    }

    [Fact]
    public void Build_ContainsScriptFirstGuidance()
    {
        var section = ShellEfficiencySection.Create();

        var lines = section.Build(DefaultContext);

        lines.ShouldContain(l => l.Contains("script", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_ContainsAntiPatternGuidance()
    {
        var section = ShellEfficiencySection.Create();

        var lines = section.Build(DefaultContext);

        lines.ShouldContain(l => l.Contains("backtick", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OrderIsAfterToolEnforcement()
    {
        ShellEfficiencySection.SectionOrder.ShouldBeGreaterThan(ToolEnforcementSection.SectionOrder);
    }
}

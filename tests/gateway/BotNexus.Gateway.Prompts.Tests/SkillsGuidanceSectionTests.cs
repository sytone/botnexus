using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Prompts.Tests;

public sealed class SkillsGuidanceSectionTests
{
    private static PromptContext DefaultContext => new() { WorkspaceDir = "C:/workspace" };

    private static PromptContext ContextWithSkillTools => new()
    {
        WorkspaceDir = "C:/workspace",
        AvailableTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skills", "read", "write" }
    };

    private static PromptContext ContextWithSkillManageTool => new()
    {
        WorkspaceDir = "C:/workspace",
        AvailableTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skill_manage", "read" }
    };

    private static PromptContext ContextWithoutSkillTools => new()
    {
        WorkspaceDir = "C:/workspace",
        AvailableTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "read", "write", "shell" }
    };

    [Fact]
    public void SectionId_IsSkillsGuidance()
    {
        SkillsGuidanceSection.Id.ShouldBe("skills-guidance");
    }

    [Fact]
    public void SectionOrder_Is55()
    {
        SkillsGuidanceSection.SectionOrder.ShouldBe(55);
    }

    [Fact]
    public void Create_ReturnsLambdaPromptSection()
    {
        var section = SkillsGuidanceSection.Create();

        section.ShouldNotBeNull();
        section.ShouldBeOfType<LambdaPromptSection>();
    }

    [Fact]
    public void Create_SectionHasCorrectOrder()
    {
        var section = SkillsGuidanceSection.Create();

        section.Order.ShouldBe(55);
    }

    [Fact]
    public void Create_SectionHasCorrectId()
    {
        var section = SkillsGuidanceSection.Create();

        section.SectionId.ShouldBe("skills-guidance");
    }

    [Fact]
    public void ShouldInclude_WhenSkillsToolAvailable_ReturnsTrue()
    {
        var section = SkillsGuidanceSection.Create();

        section.ShouldInclude(ContextWithSkillTools).ShouldBeTrue();
    }

    [Fact]
    public void ShouldInclude_WhenSkillManageToolAvailable_ReturnsTrue()
    {
        var section = SkillsGuidanceSection.Create();

        section.ShouldInclude(ContextWithSkillManageTool).ShouldBeTrue();
    }

    [Fact]
    public void ShouldInclude_WhenNoSkillToolsAvailable_ReturnsFalse()
    {
        var section = SkillsGuidanceSection.Create();

        section.ShouldInclude(ContextWithoutSkillTools).ShouldBeFalse();
    }

    [Fact]
    public void ShouldInclude_WhenNoToolsAtAll_ReturnsFalse()
    {
        var section = SkillsGuidanceSection.Create();

        section.ShouldInclude(DefaultContext).ShouldBeFalse();
    }

    [Fact]
    public void Build_ReturnsNonEmptyLines()
    {
        var section = SkillsGuidanceSection.Create();

        var lines = section.Build(ContextWithSkillTools);

        lines.ShouldNotBeEmpty();
        lines.Count.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void Build_StartsWithMarkdownHeader()
    {
        var section = SkillsGuidanceSection.Create();

        var lines = section.Build(ContextWithSkillTools);

        lines[0].ShouldBe("## Skills");
    }

    [Fact]
    public void Build_ContainsMandatoryLoadGuidance()
    {
        var section = SkillsGuidanceSection.Create();

        var lines = section.Build(ContextWithSkillTools);

        lines.ShouldContain(l => l.Contains("load", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_ContainsProactiveCreateGuidance()
    {
        var section = SkillsGuidanceSection.Create();

        var lines = section.Build(ContextWithSkillTools);

        lines.ShouldContain(l => l.Contains("create", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OrderIsAfterToolEnforcement()
    {
        SkillsGuidanceSection.SectionOrder.ShouldBeGreaterThan(ToolEnforcementSection.SectionOrder);
    }

    [Fact]
    public void OrderIsAfterShellEfficiency()
    {
        SkillsGuidanceSection.SectionOrder.ShouldBeGreaterThan(ShellEfficiencySection.SectionOrder);
    }
}

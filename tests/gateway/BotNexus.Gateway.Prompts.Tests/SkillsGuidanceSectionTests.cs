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
    public void Build_StartsWithLoadGuidance()
    {
        var section = SkillsGuidanceSection.Create();

        var lines = section.Build(ContextWithSkillTools);

        lines[0].ShouldContain("skills", Case.Insensitive);
    }

    [Fact]
    public void Create_HasXmlTag()
    {
        var section = SkillsGuidanceSection.Create();

        section.XmlTag.ShouldBe("skills");
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
    public void Build_RequiresLoadingPartiallyRelevantSkills()
    {
        var section = SkillsGuidanceSection.Create();

        var lines = section.Build(ContextWithSkillTools);
        var text = string.Join("\n", lines);

        // Mandatory-load language (Hermes pattern): even partially-relevant skills MUST be loaded.
        text.ShouldContain("partially", Case.Insensitive);
        text.ShouldContain("MUST", Case.Sensitive);
        lines.ShouldContain(l =>
            l.Contains("partially", StringComparison.OrdinalIgnoreCase)
            && l.Contains("load", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_ContainsPatchStaleSkillDirective()
    {
        var section = SkillsGuidanceSection.Create();

        var lines = section.Build(ContextWithSkillTools);

        lines.ShouldContain(l =>
            l.Contains("patch", StringComparison.OrdinalIgnoreCase)
            && l.Contains("skill_manage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_ContainsSaveReusableProcedureDirective()
    {
        var section = SkillsGuidanceSection.Create();

        var lines = section.Build(ContextWithSkillTools);
        var text = string.Join("\n", lines);

        text.ShouldContain("reusable", Case.Insensitive);
    }

    [Fact]
    public void Build_ContainsUmbrellaPreferenceOverOneOff()
    {
        var section = SkillsGuidanceSection.Create();

        var lines = section.Build(ContextWithSkillTools);

        lines.ShouldContain(l =>
            l.Contains("umbrella", StringComparison.OrdinalIgnoreCase)
            && l.Contains("one-off", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_ContainsDoNotImproviseDirective()
    {
        var section = SkillsGuidanceSection.Create();

        var lines = section.Build(ContextWithSkillTools);

        lines.ShouldContain(l => l.Contains("improvise", StringComparison.OrdinalIgnoreCase));
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

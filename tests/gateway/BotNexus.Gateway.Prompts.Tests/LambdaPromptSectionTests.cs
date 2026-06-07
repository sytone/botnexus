using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Prompts.Tests;

public sealed class LambdaPromptSectionTests
{
    private static PromptContext DefaultContext => new() { WorkspaceDir = "C:/workspace" };

    [Fact]
    public void Constructor_ThrowsOnNullBuildFunc()
    {
        var ex = Should.Throw<ArgumentNullException>(() =>
            new LambdaPromptSection(10, null!));
        ex.ParamName.ShouldBe("buildFunc");
    }

    [Fact]
    public void Order_ReturnsConfiguredValue()
    {
        var section = new LambdaPromptSection(42, static _ => ["line"]);

        section.Order.ShouldBe(42);
    }

    [Fact]
    public void SectionId_ReturnsNullByDefault()
    {
        var section = new LambdaPromptSection(10, static _ => ["line"]);

        section.SectionId.ShouldBeNull();
    }

    [Fact]
    public void SectionId_ReturnsConfiguredValue()
    {
        var section = new LambdaPromptSection(10, static _ => ["line"], sectionId: "my-section");

        section.SectionId.ShouldBe("my-section");
    }

    [Fact]
    public void ShouldInclude_ReturnsTrueWhenNoPredicateProvided()
    {
        var section = new LambdaPromptSection(10, static _ => ["line"]);

        section.ShouldInclude(DefaultContext).ShouldBeTrue();
    }

    [Fact]
    public void ShouldInclude_DelegatesToPredicate()
    {
        var section = new LambdaPromptSection(
            10,
            static _ => ["line"],
            shouldIncludeFunc: static ctx => ctx.IsMinimal);

        section.ShouldInclude(DefaultContext).ShouldBeFalse();
        section.ShouldInclude(DefaultContext with { IsMinimal = true }).ShouldBeTrue();
    }

    [Fact]
    public void Build_DelegatesToBuildFunc()
    {
        var section = new LambdaPromptSection(10, static _ => ["hello", "world"]);

        var lines = section.Build(DefaultContext);

        lines.ShouldBe(new[] { "hello", "world" });
    }

    [Fact]
    public void Build_ReceivesContext()
    {
        var section = new LambdaPromptSection(10, ctx => [$"workspace={ctx.WorkspaceDir}"]);

        var lines = section.Build(DefaultContext);

        lines.ShouldBe(new[] { "workspace=C:/workspace" });
    }
}

public sealed class ToolEnforcementSectionTests
{
    private static PromptContext DefaultContext => new() { WorkspaceDir = "C:/workspace" };

    [Fact]
    public void Create_HasCorrectOrder()
    {
        var section = ToolEnforcementSection.Create();

        section.Order.ShouldBe(32);
    }

    [Fact]
    public void Create_HasCorrectSectionId()
    {
        var section = ToolEnforcementSection.Create();

        section.SectionId.ShouldBe("tool-enforcement");
    }

    [Fact]
    public void Create_AlwaysIncluded()
    {
        var section = ToolEnforcementSection.Create();

        section.ShouldInclude(DefaultContext).ShouldBeTrue();
        section.ShouldInclude(DefaultContext with { IsMinimal = true }).ShouldBeTrue();
    }

    [Fact]
    public void Create_ProducesEnforcementGuidance()
    {
        var section = ToolEnforcementSection.Create();

        var lines = section.Build(DefaultContext);

        lines.Count.ShouldBeGreaterThan(1);
        lines[0].ShouldBe("## Tool Enforcement");
        lines.ShouldContain(l => l.Contains("execute the tool immediately"));
        lines.ShouldContain(l => l.Contains("Do not describe"));
        lines.ShouldContain(l => l.Contains("Never simulate"));
    }

    [Fact]
    public void Create_OrderIsBeforeSafety()
    {
        // Safety is at enum value 200; tool enforcement at 32 should always come first
        var section = ToolEnforcementSection.Create();
        section.Order.ShouldBeLessThan((int)PromptSection.Tools);
    }

    [Fact]
    public void Pipeline_IntegrationTest_ToolEnforcementOrderedCorrectly()
    {
        var pipeline = new PromptPipeline()
            .Add(new StaticSection(100, ["Tools content"]))
            .Add(ToolEnforcementSection.Create())
            .Add(new StaticSection(200, ["Safety content"]));

        var lines = pipeline.BuildLines(DefaultContext);

        var enforcementIdx = lines.ToList().FindIndex(l => l.Contains("Tool Enforcement"));
        var toolsIdx = lines.ToList().FindIndex(l => l.Contains("Tools content"));
        var safetyIdx = lines.ToList().FindIndex(l => l.Contains("Safety content"));

        enforcementIdx.ShouldBeGreaterThanOrEqualTo(0);
        toolsIdx.ShouldBeGreaterThan(enforcementIdx);
        safetyIdx.ShouldBeGreaterThan(toolsIdx);
    }

    private sealed class StaticSection(int order, IReadOnlyList<string> lines) : IPromptSection
    {
        public int Order => order;
        public bool ShouldInclude(PromptContext context) => true;
        public IReadOnlyList<string> Build(PromptContext context) => lines;
    }
}

public sealed class IPromptSectionSectionIdTests
{
    [Fact]
    public void DefaultImplementation_ReturnsNull()
    {
        var section = new MinimalSection();

        // The default interface implementation should return null
        IPromptSection iface = section;
        iface.SectionId.ShouldBeNull();
    }

    /// <summary>
    /// A minimal IPromptSection that does NOT override SectionId, relying on the default.
    /// </summary>
    private sealed class MinimalSection : IPromptSection
    {
        public int Order => 0;
        public bool ShouldInclude(PromptContext context) => true;
        public IReadOnlyList<string> Build(PromptContext context) => [];
    }
}

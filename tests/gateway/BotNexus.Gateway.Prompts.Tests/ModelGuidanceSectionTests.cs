using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Prompts.Tests;

public sealed class ModelGuidanceSectionTests
{
    private static PromptContext ContextWithClaude => new()
    {
        WorkspaceDir = "C:/workspace",
        Extensions = new Dictionary<string, object?> { [ModelGuidanceSection.ModelIdExtensionKey] = "claude-sonnet-4-20250514" }
    };

    private static PromptContext ContextWithGpt => new()
    {
        WorkspaceDir = "C:/workspace",
        Extensions = new Dictionary<string, object?> { [ModelGuidanceSection.ModelIdExtensionKey] = "gpt-4o" }
    };

    private static PromptContext ContextWithGemini => new()
    {
        WorkspaceDir = "C:/workspace",
        Extensions = new Dictionary<string, object?> { [ModelGuidanceSection.ModelIdExtensionKey] = "gemini-2.5-pro" }
    };

    private static PromptContext ContextWithUnknownModel => new()
    {
        WorkspaceDir = "C:/workspace",
        Extensions = new Dictionary<string, object?> { [ModelGuidanceSection.ModelIdExtensionKey] = "phi-4" }
    };

    private static PromptContext ContextWithNoModel => new()
    {
        WorkspaceDir = "C:/workspace"
    };

    [Fact]
    public void SectionId_IsModelGuidance()
    {
        ModelGuidanceSection.Id.ShouldBe("model-guidance");
    }

    [Fact]
    public void SectionOrder_Is135()
    {
        ModelGuidanceSection.SectionOrder.ShouldBe(135);
    }

    [Fact]
    public void Create_ReturnsLambdaPromptSection()
    {
        var section = ModelGuidanceSection.Create();

        section.ShouldNotBeNull();
        section.ShouldBeOfType<LambdaPromptSection>();
    }

    [Fact]
    public void Create_SectionHasCorrectOrder()
    {
        var section = ModelGuidanceSection.Create();

        section.Order.ShouldBe(135);
    }

    [Fact]
    public void Create_SectionHasCorrectId()
    {
        var section = ModelGuidanceSection.Create();

        section.SectionId.ShouldBe("model-guidance");
    }

    [Fact]
    public void ShouldInclude_WhenClaudeModel_ReturnsTrue()
    {
        var section = ModelGuidanceSection.Create();

        section.ShouldInclude(ContextWithClaude).ShouldBeTrue();
    }

    [Fact]
    public void ShouldInclude_WhenGptModel_ReturnsTrue()
    {
        var section = ModelGuidanceSection.Create();

        section.ShouldInclude(ContextWithGpt).ShouldBeTrue();
    }

    [Fact]
    public void ShouldInclude_WhenGeminiModel_ReturnsTrue()
    {
        var section = ModelGuidanceSection.Create();

        section.ShouldInclude(ContextWithGemini).ShouldBeTrue();
    }

    [Fact]
    public void ShouldInclude_WhenUnknownModel_ReturnsFalse()
    {
        var section = ModelGuidanceSection.Create();

        section.ShouldInclude(ContextWithUnknownModel).ShouldBeFalse();
    }

    [Fact]
    public void ShouldInclude_WhenNoModelId_ReturnsFalse()
    {
        var section = ModelGuidanceSection.Create();

        section.ShouldInclude(ContextWithNoModel).ShouldBeFalse();
    }

    [Fact]
    public void Build_ForClaude_ReturnsClaudeGuidance()
    {
        var section = ModelGuidanceSection.Create();

        var lines = section.Build(ContextWithClaude);

        lines.ShouldNotBeEmpty();
        lines[0].ShouldContain("edit tool", Case.Insensitive);
    }

    [Fact]
    public void Build_ForGpt_ReturnsGptGuidance()
    {
        var section = ModelGuidanceSection.Create();

        var lines = section.Build(ContextWithGpt);

        lines.ShouldNotBeEmpty();
        lines[0].ShouldContain("memory", Case.Insensitive);
        lines.ShouldContain(l => l.Contains("memory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_ForGemini_ReturnsGeminiGuidance()
    {
        var section = ModelGuidanceSection.Create();

        var lines = section.Build(ContextWithGemini);

        lines.ShouldNotBeEmpty();
        lines[0].ShouldContain("absolute paths", Case.Insensitive);
        lines.ShouldContain(l => l.Contains("absolute path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_ForUnknownModel_ReturnsEmptyList()
    {
        var section = ModelGuidanceSection.Create();

        var lines = section.Build(ContextWithUnknownModel);

        lines.ShouldBeEmpty();
    }

    [Fact]
    public void ModelIdExtensionKey_IsModelId()
    {
        ModelGuidanceSection.ModelIdExtensionKey.ShouldBe("modelId");
    }
}

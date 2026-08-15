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

    /// <summary>
    /// #2433 INVERTS the pre-existing expectation here, deliberately. This test previously asserted
    /// <c>ShouldBeFalse</c> -- i.e. it PINNED the fail-open: an unrecognised model was dropped from
    /// the section entirely and silently received zero behavioural guidance. The registry's default
    /// rung is mandatory precisely so that state is unreachable, so the correct assertion is now the
    /// opposite one.
    /// </summary>
    [Fact]
    public void ShouldInclude_WhenUnknownModel_ReturnsTrue_BecauseTheDefaultRungAlwaysApplies()
    {
        var section = ModelGuidanceSection.Create();

        section.ShouldInclude(ContextWithUnknownModel).ShouldBeTrue();
    }

    [Fact]
    public void ShouldInclude_WhenNoModelId_ReturnsTrue_BecauseTheDefaultRungAlwaysApplies()
    {
        var section = ModelGuidanceSection.Create();

        section.ShouldInclude(ContextWithNoModel).ShouldBeTrue();
    }

    [Fact]
    public void Build_ForClaude_ReturnsClaudeGuidance()
    {
        var section = ModelGuidanceSection.Create();

        var lines = section.Build(ContextWithClaude);

        lines.ShouldNotBeEmpty();
        lines.ShouldContain(l => l.Contains("edit tool", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_ForGpt_ReturnsGptGuidance()
    {
        var section = ModelGuidanceSection.Create();

        var lines = section.Build(ContextWithGpt);

        lines.ShouldNotBeEmpty();
        lines.ShouldContain(l => l.Contains("memory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_ForGemini_ReturnsGeminiGuidance()
    {
        var section = ModelGuidanceSection.Create();

        var lines = section.Build(ContextWithGemini);

        lines.ShouldNotBeEmpty();
        lines.ShouldContain(l => l.Contains("absolute path", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The headline acceptance criterion of #2433: an unknown model resolves to the DEFAULT rung,
    /// never to an empty list. The previous assertion here was <c>ShouldBeEmpty</c>.
    /// </summary>
    [Fact]
    public void Build_ForUnknownModel_ReturnsTheConservativeDefaultRung()
    {
        var section = ModelGuidanceSection.Create();

        var lines = section.Build(ContextWithUnknownModel);

        lines.ShouldNotBeEmpty();
        lines.ShouldBe(ModelGuidanceSection.Default().Select(rule => rule.Text!).ToList());
    }

    [Fact]
    public void Build_ForUnknownModel_ContainsNoFamilySpecificRule()
    {
        var section = ModelGuidanceSection.Create();

        var lines = section.Build(ContextWithUnknownModel);

        lines.ShouldNotContain(l => l.Contains("edit tool", StringComparison.OrdinalIgnoreCase));
        lines.ShouldNotContain(l => l.Contains("absolute path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_ForClaude_OverlaysTheDefaultRatherThanReplacingIt()
    {
        // Overlay-by-default (#2433): a family rung ADDS to the shared intent, it does not restate
        // it. Every default rule must survive into the Claude resolution.
        var section = ModelGuidanceSection.Create();

        var lines = section.Build(ContextWithClaude);

        foreach (var rule in ModelGuidanceSection.Default())
        {
            lines.ShouldContain(rule.Text!);
        }
    }

    [Fact]
    public void Build_ForFamilyServedUnderAVanityId_StillResolvesViaTheProviderFallback()
    {
        // #3104's provider fallback must keep working through the registry.
        var section = ModelGuidanceSection.Create();

        var lines = section.Build(new PromptContext
        {
            WorkspaceDir = "C:/workspace",
            Extensions = new Dictionary<string, object?>
            {
                [ModelGuidanceSection.ModelIdExtensionKey] = "vanity-model-x",
                [ModelGuidanceSection.ProviderIdExtensionKey] = "anthropic"
            }
        });

        lines.ShouldContain(l => l.Contains("edit tool", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Section_DeclaresADefaultRung_SoTheRegistryCanFreezeIt()
    {
        PromptVariantRegistry.Shared.HasSection(ModelGuidanceSection.Id).ShouldBeTrue();
        ModelGuidanceSection.Default().ShouldNotBeEmpty();
    }

    [Fact]
    public void ModelIdExtensionKey_IsModelId()
    {
        ModelGuidanceSection.ModelIdExtensionKey.ShouldBe("modelId");
    }
}

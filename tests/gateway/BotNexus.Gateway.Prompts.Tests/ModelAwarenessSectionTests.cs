using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Prompts.Tests;

/// <summary>
/// Pins the <c>model-awareness</c> section (#2436): it must resolve through the frozen variant
/// registry like every other laddered section, it must never fall open to silence, and its default
/// rung must actually carry the agnostic-vs-specific classification instruction the epic exists for.
/// </summary>
/// <remarks>
/// New file by construction: <c>ModelGuidanceSectionGptRungTests</c>, <c>PromptVariantConformanceTests</c>
/// and <c>PromptVariantRegistryTests</c> are reserved by an open PR and are not touched here.
/// </remarks>
public class ModelAwarenessSectionTests
{
    private static IReadOnlyList<string> Build(string? modelId, string? providerId = null)
    {
        var section = ModelAwarenessSection.Create();
        var context = new PromptContext
        {
            WorkspaceDir = "C:/workspace",
            Extensions = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [ModelGuidanceSection.ModelIdExtensionKey] = modelId,
                [ModelGuidanceSection.ProviderIdExtensionKey] = providerId
            }
        };

        return section.ShouldInclude(context) ? section.Build(context) : [];
    }

    [Fact]
    public void SectionIsRegisteredInTheFrozenVariantRegistry()
    {
        // The section must be laddered, not a bare string array. Without a registered default rung
        // Resolve returns empty and the whole section silently disappears.
        Assert.True(PromptVariantRegistry.Shared.HasSection(ModelAwarenessSection.Id));
    }

    [Theory]
    [InlineData("claude-opus-5", "anthropic")]
    [InlineData("gpt-5.6", "openai")]
    [InlineData("gemini-2.5-pro", "google")]
    [InlineData("some-vendor-model-nobody-has-heard-of", "openrouter")]
    [InlineData(null, null)]
    public void EveryModelIncludingUnknownOnesGetsTheDefaultRung(string? modelId, string? providerId)
    {
        // The regression #2433 removed was fail-open on an unrecognised family. A model-awareness
        // section that vanishes for unknown models is exactly the hole this epic is closing, so the
        // assertion is over the WHOLE default rung, not merely "non-empty".
        var lines = Build(modelId, providerId);

        Assert.All(
            ModelAwarenessSection.Default().Select(rule => rule.Text!),
            expected => Assert.Contains(expected, lines));
    }

    [Fact]
    public void DefaultRungTellsTheAgentToClassifyBaseFileEditsAndNamesTheDiscoveryTool()
    {
        var lines = Build("gpt-5.6", "openai");

        // The two clauses that carry the issue's intent: base file = contract, and the tool is how
        // the agnostic-vs-specific question is answered from data instead of intuition.
        Assert.Contains(lines, line => line.Contains("agnostic", StringComparison.Ordinal)
                                       && line.Contains("model-specific", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains(ModelAwarenessSection.DiscoveryToolName, StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultRungDocumentsTheVariantFilenameGrammar()
    {
        // "Grammar documentation reachable" is an explicit acceptance clause: an agent that authors
        // a mis-named variant gets a file that is silently never read.
        var lines = Build("claude-opus-5", "anthropic");
        Assert.Contains(lines, line => line.Contains("AGENTS.gpt-5.md", StringComparison.Ordinal));
    }

    [Fact]
    public void ClaudeRungOverlaysTheFamilyShapedProseWarningAndOtherFamiliesDoNot()
    {
        var claudeWarning = ModelAwarenessSection.Claude().Single().Text!;

        Assert.Contains(claudeWarning, Build("claude-opus-5", "anthropic"));
        Assert.DoesNotContain(claudeWarning, Build("gpt-5.6", "openai"));
        Assert.DoesNotContain(claudeWarning, Build("some-unknown-model", null));
    }

    [Fact]
    public void SectionOrderPlacesAwarenessImmediatelyAfterGuidance()
    {
        // The two sections read as one block: "here is how you behave" then "here is why those rules
        // are yours specifically". A gap between them lets an unrelated section split the framing.
        Assert.Equal(ModelGuidanceSection.SectionOrder + 1, ModelAwarenessSection.SectionOrder);
    }
}

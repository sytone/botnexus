using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Agent.Providers.Core.Tests.Registry;

/// <summary>
/// Pins the de-duplicated capability predicates that replaced the four copies of the literal
/// substring list (issue #2374). The Opus 5 rows are the reason the issue exists; the Opus 4.x rows
/// are the no-regression guard proving the rewrite preserved every prior classification.
/// </summary>
public class ModelCapabilityHeuristicsTests
{
    [Theory]
    // New generation -- these were classified by NONE of the old substring lists.
    [InlineData("claude-opus-5", true)]
    [InlineData("claude-opus-5.1", true)]
    [InlineData("claude-opus-6", true)]
    // No regression: exactly what the old opus-4-6 / opus-4.6 / opus-4-8 / opus-4.8 list matched.
    [InlineData("claude-opus-4.6", true)]
    [InlineData("opus-4-6", true)]
    [InlineData("claude-opus-4.8", true)]
    [InlineData("opus-4-8", true)]
    [InlineData("claude-sonnet-4.6", true)]
    [InlineData("sonnet-4-6", true)]
    // No regression: what it deliberately did NOT match.
    [InlineData("claude-opus-4.5", false)]
    [InlineData("claude-opus-4-5-20250929", false)]
    [InlineData("claude-sonnet-4.5", false)]
    [InlineData("claude-haiku-4.5", false)]
    [InlineData("gpt-5.4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAdaptiveThinkingModel_GatesByParsedVersion(string? modelId, bool expected) =>
        Assert.Equal(expected, ModelCapabilityHeuristics.IsAdaptiveThinkingModel(modelId));

    [Fact]
    public void IsAdaptiveThinkingModel_OrdersNumericallyNotByCharacter()
    {
        // The old prefix parser read one character: '5' in "4.50" < '6', so 4.50 was excluded.
        Assert.True(ModelCapabilityHeuristics.IsAdaptiveThinkingModel("claude-opus-4.50"));
    }

    [Theory]
    [InlineData("claude-opus-5", true)]
    [InlineData("claude-opus-4.6", true)]
    [InlineData("claude-opus-4.8", true)]
    [InlineData("claude-opus-4.50", true)]
    [InlineData("claude-opus-4.5", false)]
    [InlineData("gpt-5.2", true)]
    [InlineData("gpt-5.4", true)]
    [InlineData("gpt-5.1", false)]
    [InlineData("gpt-4.1", false)]
    [InlineData("claude-sonnet-4.6", false)]
    [InlineData("not-a-model", false)]
    [InlineData(null, false)]
    public void SupportsExtraHighThinking_GatesByParsedVersion(string? modelId, bool expected) =>
        Assert.Equal(expected, ModelCapabilityHeuristics.SupportsExtraHighThinking(modelId));

    [Theory]
    [InlineData("claude-opus-5", true)]
    [InlineData("claude-opus-4.5", true)]
    [InlineData("claude-opus-4-5-20250929", true)]
    [InlineData("claude-sonnet-4", true)]
    [InlineData("claude-sonnet-4-20250514", true)]
    [InlineData("gpt-5", true)]
    [InlineData("gpt-5.4", true)]
    [InlineData("o3", true)]
    [InlineData("o4-mini", true)]
    [InlineData("gemini-3-pro-preview", true)]
    [InlineData("gemini-3.1-pro-preview", true)]
    [InlineData("grok-code-fast-1", true)]
    [InlineData("claude-3-5-haiku-20241022", false)]
    [InlineData("gpt-4.1", false)]
    [InlineData("gpt-4o", false)]
    [InlineData("gemini-2.5-pro", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsReasoningModel_GatesByParsedVersion(string? modelId, bool expected) =>
        Assert.Equal(expected, ModelCapabilityHeuristics.IsReasoningModel(modelId));
}

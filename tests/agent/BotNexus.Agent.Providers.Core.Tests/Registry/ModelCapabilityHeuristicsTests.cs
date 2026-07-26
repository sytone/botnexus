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

    /// <summary>
    /// Fail OPEN to modern behaviour on an unparseable Claude id (issue #2374 follow-up). A brand
    /// new Claude generation whose id shape we have never seen must get CURRENT-generation adaptive
    /// thinking, not silently degrade to the legacy token-budget protocol.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-next")]
    [InlineData("claude-opus-latest")]
    [InlineData("claude-sonnet-next")]
    [InlineData("claude-next")]
    [InlineData("anthropic/claude-opus-preview")]
    public void IsAdaptiveThinkingModel_FailsOpenToModernOnUnparseableClaudeIds(string modelId)
    {
        // Precondition: the id genuinely does not yield a version -- so the assertion below is not
        // vacuously satisfied by the ordinary numeric gate.
        Assert.False(ModelFamilyVersion.TryParse(modelId, "opus", out _));
        Assert.False(ModelFamilyVersion.TryParse(modelId, "sonnet", out _));
        Assert.True(ModelCapabilityHeuristics.IsAdaptiveThinkingModel(modelId));
    }

    /// <summary>
    /// The fail-open is NOT a blanket true: unrelated vendors and non-token substring collisions
    /// must still fail closed.
    /// </summary>
    [Theory]
    [InlineData("octopus5")]
    [InlineData("octopus-next")]
    [InlineData("gpt-5-preview")]
    [InlineData("gemini-3-pro-preview")]
    [InlineData("grok-code-fast-1")]
    [InlineData("not-a-model")]
    [InlineData("clauded-out")]
    [InlineData("mistral-large-latest")]
    public void IsAdaptiveThinkingModel_DoesNotFailOpenForNonClaudeIds(string modelId) =>
        Assert.False(ModelCapabilityHeuristics.IsAdaptiveThinkingModel(modelId));

    /// <summary>
    /// Version-first id ordering (<c>claude-4.7-opus</c>, used by SAP AI Core) must classify
    /// identically to the family-first spelling of the same model.
    /// </summary>
    [Theory]
    [InlineData("claude-4.7-opus", true)]
    [InlineData("claude-4.6-opus", true)]
    [InlineData("claude-5-opus", true)]
    [InlineData("claude-4.6-sonnet", true)]
    [InlineData("claude-4.5-opus", false)]
    [InlineData("claude-3.7-sonnet", false)]
    public void IsAdaptiveThinkingModel_HandlesVersionFirstIdOrdering(string modelId, bool expected)
    {
        Assert.Equal(expected, ModelCapabilityHeuristics.IsAdaptiveThinkingModel(modelId));

        // Not vacuous: prove the version really parsed rather than the fail-open firing.
        var family = modelId.Contains("opus", StringComparison.Ordinal) ? "opus" : "sonnet";
        Assert.True(ModelFamilyVersion.TryParse(modelId, family, out var version));
        Assert.NotEqual(0, version.Major);
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

    /// <summary>
    /// The extra-high fail-open is narrower than the adaptive one: only an unversioned OPUS (or a
    /// bare unversioned Claude) gets the top tiers, because Sonnet and Haiku never had them.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-next", true)]
    [InlineData("claude-next", true)]
    [InlineData("claude-sonnet-next", false)]
    [InlineData("claude-haiku-next", false)]
    [InlineData("octopus-next", false)]
    [InlineData("gpt-next", false)]
    public void SupportsExtraHighThinking_FailsOpenOnlyForUnversionedOpusClassIds(string modelId, bool expected) =>
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

    /// <summary>
    /// An unversioned Claude id must also be treated as reasoning-capable -- failing closed here
    /// would strip thinking entirely from a new generation (issue #2374).
    /// </summary>
    [Theory]
    [InlineData("claude-opus-next", true)]
    [InlineData("claude-sonnet-next", true)]
    [InlineData("claude-next", true)]
    [InlineData("claude-haiku-next", false)]
    [InlineData("octopus-next", false)]
    [InlineData("mistral-large-latest", false)]
    public void IsReasoningModel_FailsOpenOnUnversionedClaudeIds(string modelId, bool expected) =>
        Assert.Equal(expected, ModelCapabilityHeuristics.IsReasoningModel(modelId));

    /// <summary>
    /// Date-stamped ids must never be read as a huge minor version: <c>claude-opus-4-20250514</c>
    /// is Opus 4.0, so it stays BELOW the 4.6 adaptive floor.
    /// </summary>
    [Fact]
    public void DateStampedIdsParseAsMajorOnlyAndStayBelowTheAdaptiveFloor()
    {
        Assert.True(ModelFamilyVersion.TryParse("claude-opus-4-20250514", "opus", out var version));
        Assert.Equal(new ModelVersion(4, 0), version);
        Assert.False(ModelCapabilityHeuristics.IsAdaptiveThinkingModel("claude-opus-4-20250514"));
        Assert.False(ModelCapabilityHeuristics.SupportsExtraHighThinking("claude-opus-4-20250514"));
        Assert.True(ModelCapabilityHeuristics.IsReasoningModel("claude-opus-4-20250514"));
    }
}

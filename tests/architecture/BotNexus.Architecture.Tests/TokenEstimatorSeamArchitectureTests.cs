using BotNexus.Domain.Text;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// #3655: the token-estimate ratio must have exactly ONE definition, and every consumer that reports
/// or budgets tokens must consult it.
/// </summary>
/// <remarks>
/// <para>
/// The defect was not that <c>chars / 4</c> was wrong in one place - it was that it was written out
/// in four independent places, so fixing the compactor alone would have left <c>/context</c>, the
/// memory budget and the skills report reporting a quarter of a CJK session's real cost. A point fix
/// in one call site is indistinguishable from a real fix at review time; this fence is what makes the
/// difference detectable.
/// </para>
/// <para>
/// These assert on SOURCE rather than on computed values deliberately. The failure mode is a call
/// site that silently stops consulting the seam and re-inlines its own divisor - a behavioural test
/// on the seam itself stays green throughout, because the seam is still correct. Only reading the
/// call sites can detect a consumer that walked away.
/// </para>
/// </remarks>
public sealed class TokenEstimatorSeamArchitectureTests : ArchitectureTest
{
    /// <summary>
    /// Every file that previously hard-coded the ratio must now name the shared seam. Listed by path
    /// so that deleting a consumer is a deliberate act rather than a silently shrinking sweep.
    /// </summary>
    public static TheoryData<string[]> Consumers() => new()
    {
        new[] { "src", "gateway", "BotNexus.Gateway", "Sessions", "LlmSessionCompactor.cs" },
        new[] { "src", "gateway", "BotNexus.Gateway.Sessions", "SessionContextProjector.cs" },
        new[] { "src", "gateway", "BotNexus.Gateway", "Isolation", "InProcessIsolationStrategy.cs" },
        new[] { "src", "gateway", "BotNexus.Memory", "MemoryPromptBudget.cs" },
        new[] { "src", "extensions", "BotNexus.Extensions.Skills", "SkillsCommandContributor.cs" }
    };

    [Theory]
    [MemberData(nameof(Consumers))]
    public void EveryTokenConsumer_ConsultsTheSharedEstimator(string[] segments)
    {
        var path = Repository.Path(segments);
        File.Exists(path).ShouldBeTrue($"{string.Join('/', segments)} is a declared #3655 consumer but does not exist.");

        File.ReadAllText(path).Contains(nameof(TokenEstimator), StringComparison.Ordinal).ShouldBeTrue(
            $"{segments[^1]} must estimate tokens through {nameof(TokenEstimator)} rather than " +
            "re-deriving a divisor. Four independent copies of this heuristic is what made the CJK " +
            "under-count survive in three call sites after the fourth was fixed (#3655).");
    }

    /// <summary>
    /// The ratio itself is declared once. <c>MemoryPromptBudget.CharsPerToken</c> is retained as a
    /// public re-export for its existing callers and tests, but it must FORWARD rather than redeclare
    /// - two constants that merely happen to be equal today are two constants tomorrow.
    /// </summary>
    [Fact]
    public void TheRatioIsDeclaredOnce_AndMemoryForwardsToIt()
    {
        var source = File.ReadAllText(
            Repository.Path("src", "gateway", "BotNexus.Memory", "MemoryPromptBudget.cs"));

        source.Contains($"CharsPerToken = {nameof(TokenEstimator)}.{nameof(TokenEstimator.CharsPerToken)}", StringComparison.Ordinal).ShouldBeTrue(
            "MemoryPromptBudget.CharsPerToken must forward to the shared constant, not redeclare 4.");
    }

    /// <summary>
    /// Anti-vacuity for the whole fixture: the seam must actually discriminate by script. If
    /// <see cref="TokenEstimator"/> ever degenerates back to a flat divide, every "consults the seam"
    /// assertion above would still pass while the defect is fully reintroduced.
    /// </summary>
    [Fact]
    public void TheSeamIsGenuinelyScriptAware_NotAFlatDivideWearingANewName()
    {
        var latin = TokenEstimator.EstimateTokens(new string('a', 1_000));
        var cjk = TokenEstimator.EstimateTokens(new string('\u6587', 1_000));

        latin.ShouldBe(250);
        cjk.ShouldBeGreaterThan(latin * 3,
            "a CJK character costs roughly a whole token. If this collapses to 250 the estimator is " +
            "a flat chars/4 again and #3655 is back, regardless of how many files reference it.");
    }
}

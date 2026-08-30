using BotNexus.Domain.Primitives;
using BotNexus.Domain.Text;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// #3655: the compaction token estimate must be script-aware. A flat <c>chars / 4</c> is calibrated
/// for Latin script and under-counts CJK text by roughly four times, so a CJK-heavy session reported
/// about a quarter of its real context consumption and the estimate-based trigger did not fire until
/// the session was already far past its threshold.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non-vacuity.</b> Every assertion here is keyed on a value that is FALSE under the flat divide.
/// <see cref="ShouldCompact_CjkSessionJustPastThreshold_TriggersWithoutAnyProviderCount_Ac4"/> is the
/// anchor: its session carries no <c>lastProviderPromptTokens</c> metadata and its byte-bloat trigger
/// is disabled, so the estimate is the only signal available. Reverting
/// <see cref="TokenEstimator.WeightedCharUnits(string?)"/> to charge 1 unit per character - i.e. back
/// to <c>chars / 4</c> - reddens that test by name, because the same transcript then estimates at a
/// quarter of the threshold instead of just over it.
/// </para>
/// <para>
/// These are approximation tests, not tokenizer tests. The tolerances are deliberately wide (AC2
/// allows +/-50%): the contract is "the same order of magnitude as real provider usage", not an exact
/// count, and pinning an exact count would make the heuristic unimprovable.
/// </para>
/// </remarks>
public sealed class CjkTokenEstimationTests
{
    private static readonly AgentId TestAgent = AgentId.From("test-agent");

    /// <summary>A common Han ideograph - one token under a typical BPE tokenizer.</summary>
    private const char Han = '文';

    /// <summary>A Hiragana syllable, to prove the ranges are not Han-only.</summary>
    private const char Kana = 'あ';

    /// <summary>A Hangul syllable, likewise.</summary>
    private const char Hangul = '한';

    private static readonly LlmModel TestModel = new(
        Id: "test-model",
        Name: "Test Model",
        Api: "test-api",
        Provider: "test-provider",
        BaseUrl: "https://example.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 32000,
        MaxTokens: 4096);

    // ── AC2: CJK is no longer under-counted ──────────────────────────────────

    /// <summary>
    /// AC2: 10,000 CJK characters must estimate within 50% of 10,000 tokens. The pre-fix
    /// implementation returned 2,500, which is outside the band by construction, so this test cannot
    /// pass against it.
    /// </summary>
    [Theory]
    [InlineData(Han)]
    [InlineData(Kana)]
    [InlineData(Hangul)]
    public void EstimateTokens_TenThousandCjkChars_IsWithinFiftyPercentOfTenThousand_Ac2(char cjk)
    {
        var estimate = TokenEstimator.EstimateTokens(new string(cjk, 10_000));

        estimate.ShouldBeInRange(5_000, 15_000,
            $"10,000 '{cjk}' characters cost roughly 10,000 real tokens. The pre-#3655 flat divide " +
            "returned 2,500, which under-counts by 4x and lets a CJK session exhaust its window " +
            "before the estimate trigger fires.");

        // Explicitly not the defect's answer, so a partial revert cannot pass this quietly.
        estimate.ShouldNotBe(2_500);
    }

    /// <summary>
    /// Astral-plane CJK (Extension B) is charged as ONE token, not as two non-CJK surrogate halves.
    /// Classifying the halves separately would charge a single ideograph at 1/2 a token - a worse
    /// under-count than the defect being fixed.
    /// </summary>
    [Fact]
    public void EstimateTokens_AstralPlaneCjk_ChargesOneTokenPerIdeographNotPerSurrogate()
    {
        // U+20000, CJK Unified Ideographs Extension B: 2 UTF-16 code units, 1 code point.
        var text = string.Concat(Enumerable.Repeat("\U00020000", 1_000));

        text.Length.ShouldBe(2_000, "the fixture must genuinely be surrogate pairs, or this proves nothing");
        TokenEstimator.EstimateTokens(text).ShouldBeInRange(500, 1_500);
    }

    // ── AC3: the common case is unchanged ────────────────────────────────────

    /// <summary>
    /// AC3: 10,000 Latin characters still estimate ~2,500 tokens. This is the regression guard - a
    /// fix that made every session look four times larger would trigger constant compaction on the
    /// overwhelmingly common case.
    /// </summary>
    [Fact]
    public void EstimateTokens_TenThousandLatinChars_StillEstimatesTwentyFiveHundred_Ac3()
    {
        TokenEstimator.EstimateTokens(new string('a', 10_000)).ShouldBe(2_500);
    }

    /// <summary>
    /// AC3, extended: Latin prose with punctuation and whitespace is unchanged too. Only the CJK
    /// ranges are re-weighted; nothing else may shift.
    /// </summary>
    [Fact]
    public void EstimateTokens_LatinProse_IsIdenticalToTheHistoricalDivide_Ac3()
    {
        const string prose = "The quick brown fox jumps over the lazy dog, repeatedly and at length.";

        TokenEstimator.EstimateTokens(prose).ShouldBe(prose.Length / 4);
    }

    /// <summary>Null and empty cost nothing, and cannot throw.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EstimateTokens_NullOrEmpty_IsZero(string? text)
    {
        TokenEstimator.EstimateTokens(text).ShouldBe(0);
    }

    // ── AC5: mixed script lands between the two ──────────────────────────────

    /// <summary>
    /// AC5: a half-CJK, half-Latin transcript estimates strictly between the two single-script
    /// results. A blend that collapsed onto either endpoint would mean the weighting is not actually
    /// per-character.
    /// </summary>
    [Fact]
    public void EstimateTokens_MixedScript_LandsStrictlyBetweenTheSingleScriptResults_Ac5()
    {
        var latin = TokenEstimator.EstimateTokens(new string('a', 10_000));
        var cjk = TokenEstimator.EstimateTokens(new string(Han, 10_000));
        var mixed = TokenEstimator.EstimateTokens(new string(Han, 5_000) + new string('a', 5_000));

        latin.ShouldBeLessThan(cjk, "the whole premise is that CJK costs more per character");
        mixed.ShouldBeGreaterThan(latin);
        mixed.ShouldBeLessThan(cjk);
    }

    // ── AC1: one seam, consulted by every consumer ───────────────────────────

    /// <summary>
    /// AC1: the entry-cost helper the compactor sums is the shared estimator, so a CJK entry costs
    /// more units than a Latin entry of identical length. Asserting through
    /// <see cref="SessionContextProjector"/> rather than through <see cref="TokenEstimator"/> proves
    /// the seam is actually WIRED, which a direct estimator test cannot.
    /// </summary>
    [Fact]
    public void GetLiveContextTokenUnits_ChargesCjkMoreThanLatinOfEqualLength_Ac1()
    {
        var latin = new SessionEntry { Role = MessageRole.User, Content = new string('a', 100) };
        var cjk = new SessionEntry { Role = MessageRole.User, Content = new string(Han, 100) };

        SessionContextProjector.GetLiveContextCharCost(latin)
            .ShouldBe(SessionContextProjector.GetLiveContextCharCost(cjk),
                "raw character cost is script-blind by design - it is the #1599 bloat unit");

        SessionContextProjector.GetLiveContextTokenUnits(cjk)
            .ShouldBeGreaterThan(SessionContextProjector.GetLiveContextTokenUnits(latin),
                "the TOKEN unit must be script-aware, which is the whole of #3655");
    }

    /// <summary>
    /// AC1 + #3536: the weighted cost still charges for every payload-bearing field, not just
    /// <c>Content</c>. A script-aware estimator that silently dropped <c>ToolArgs</c> would trade one
    /// under-count for another.
    /// </summary>
    [Fact]
    public void GetLiveContextTokenUnits_ChargesToolArgsAndThinkingContent_Ac1()
    {
        var contentOnly = new SessionEntry { Role = MessageRole.Tool, Content = new string(Han, 10) };
        var everything = new SessionEntry
        {
            Role = MessageRole.Tool,
            Content = new string(Han, 10),
            ToolArgs = new string(Han, 10),
            ThinkingContent = new string(Han, 10)
        };

        SessionContextProjector.GetLiveContextTokenUnits(everything)
            .ShouldBe(SessionContextProjector.GetLiveContextTokenUnits(contentOnly) * 3);
    }

    // ── AC4: the non-vacuity trigger clause ──────────────────────────────────

    /// <summary>
    /// AC4: a CJK session just past <c>ContextWindowTokens * TokenThresholdRatio</c> in REAL tokens,
    /// with no provider prompt-token metadata recorded, must make <c>ShouldCompact</c> return true.
    /// </summary>
    /// <remarks>
    /// This is the clause the issue exists for, and it is deliberately constructed so that the flat
    /// divide cannot satisfy it: the transcript is 121,000 CJK characters against a 120,000-token
    /// threshold. Script-weighted that is ~121,000 tokens (over); under <c>chars / 4</c> it is
    /// ~30,250 (a quarter of the threshold, comfortably under). The byte-bloat trigger is disabled
    /// and no provider count exists, so the estimate is the only signal that can fire - there is no
    /// second mechanism that could make this pass for the wrong reason.
    /// </remarks>
    [Fact]
    public void ShouldCompact_CjkSessionJustPastThreshold_TriggersWithoutAnyProviderCount_Ac4()
    {
        var session = CreateCjkSession(totalCjkChars: 121_000);
        var options = LiveLikeOptions();

        session.Metadata.ShouldNotContainKey(
            LlmSessionCompactor.ProviderPromptTokensMetadataKey,
            "AC4 requires the provider signal to be absent, otherwise the estimate is not under test");

        var compactor = CreateCompactor(new ListLogger<LlmSessionCompactor>());

        compactor.ShouldCompact(session.Session, options).ShouldBeTrue(
            "121,000 CJK characters cost ~121,000 real tokens, past the 120,000 threshold. Under the " +
            "pre-#3655 flat chars/4 divide this estimates ~30,250 and the session never compacts - " +
            "reverting TokenEstimator to a flat divide must redden this test.");
    }

    /// <summary>
    /// The opposite bound: a CJK session comfortably UNDER the threshold must not trigger. Without
    /// this, "always return true" would satisfy AC4, which is not a fix.
    /// </summary>
    [Fact]
    public void ShouldCompact_CjkSessionWellUnderThreshold_DoesNotTrigger_Ac4()
    {
        var session = CreateCjkSession(totalCjkChars: 10_000);

        CreateCompactor(new ListLogger<LlmSessionCompactor>())
            .ShouldCompact(session.Session, LiveLikeOptions())
            .ShouldBeFalse();
    }

    /// <summary>
    /// AC7 corroboration: an equivalently sized LATIN session at the same character count must still
    /// NOT trigger, because 121,000 Latin characters really are only ~30,250 tokens. This pins that
    /// the change is a re-weighting rather than a blanket inflation of every estimate.
    /// </summary>
    [Fact]
    public void ShouldCompact_LatinSessionOfTheSameCharacterCount_StillDoesNotTrigger_Ac7()
    {
        var session = CreateSession(new string('a', 121_000));

        CreateCompactor(new ListLogger<LlmSessionCompactor>())
            .ShouldCompact(session.Session, LiveLikeOptions())
            .ShouldBeFalse(
                "121,000 Latin characters are ~30,250 tokens - genuinely under the 120,000 threshold. " +
                "A fix that inflated every script would compact healthy English sessions constantly.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Window 200000 * ratio 0.6 => 120000, matching the existing trigger suites.</summary>
    private static CompactionOptions LiveLikeOptions() => new()
    {
        PreservedTurns = 3,
        ContextWindowTokens = 200_000,
        TokenThresholdRatio = 0.6,
        // Disable the #1599 byte trigger: CJK is 3 bytes per character in UTF-8 and is ALREADY
        // script-aware there, so leaving it on would let these tests pass without the estimator
        // change at all. Isolating the token decision is what makes AC4 non-vacuous.
        LargestEntryBytesThreshold = 0,
        SummarizationModel = TestModel.Id
    };

    private static GatewaySession CreateCjkSession(int totalCjkChars)
        => CreateSession(new string(Han, totalCjkChars));

    /// <summary>
    /// Splits <paramref name="text"/> across several visible entries so the estimate is exercised as
    /// a SUM over entries rather than as a single string - the unit-then-convert path that must not
    /// discard per-entry remainders.
    /// </summary>
    private static GatewaySession CreateSession(string text)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(Guid.NewGuid().ToString("N")),
            AgentId = TestAgent
        };

        const int parts = 10;
        var chunk = text.Length / parts;
        var entries = new List<SessionEntry>(parts);
        for (var i = 0; i < parts; i++)
        {
            var start = i * chunk;
            var length = i == parts - 1 ? text.Length - start : chunk;
            entries.Add(new SessionEntry
            {
                Role = i % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
                Content = text.Substring(start, length)
            });
        }

        session.AddEntries(entries);
        return session;
    }

    private static LlmSessionCompactor CreateCompactor(ILogger<LlmSessionCompactor> logger)
    {
        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();
        models.Register(TestModel.Provider, TestModel);
        return new LlmSessionCompactor(new LlmClient(providers, models), logger);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception));
    }
}

using BotNexus.Agent.Providers.Copilot;

namespace BotNexus.Agent.Providers.Copilot.Tests;

/// <summary>
/// Direct, falsifiable coverage of <see cref="CopilotTextDeltaNormalizer"/> (#2443).
/// </summary>
/// <remarks>
/// Before this file the normalizer had zero direct tests: it was exercised only incidentally by two
/// parity tests that synthesise <c>"\r\n" + fragment</c>, i.e. tests that assume the very wire shape
/// under question. A content-mutating transform on the most-used model family needs its no-fire
/// cases pinned at least as hard as its fire cases, because the expensive failure mode is silently
/// deleting legitimate model content, not failing to strip framing.
/// </remarks>
public class CopilotTextDeltaNormalizerTests
{
    // Fire cases -------------------------------------------------------------

    [Fact]
    public void Normalize_Gpt56_SingleLeadingCrlf_IsStripped()
        => CopilotTextDeltaNormalizer.Normalize("gpt-5.6", "\r\nHello").ShouldBe("Hello");

    [Fact]
    public void Normalize_Gpt56_RepeatedLeadingCrlf_AllPairsStripped()
        => CopilotTextDeltaNormalizer.Normalize("gpt-5.6-sol", "\r\n\r\n\r\nHello").ShouldBe("Hello");

    [Fact]
    public void Normalize_Gpt56_ModelIdMatchIsCaseInsensitive()
        => CopilotTextDeltaNormalizer.Normalize("GPT-5.6-SOL", "\r\nHi").ShouldBe("Hi");

    // No-fire cases - the ones that matter most --------------------------------

    [Fact]
    public void Normalize_NonGpt56Model_IsByteIdenticalPassthrough()
    {
        const string delta = "\r\n\r\nclaude keeps its bytes";

        CopilotTextDeltaNormalizer.Normalize("claude-sonnet-4", delta).ShouldBe(delta);
    }

    [Fact]
    public void Normalize_Gpt5NotGpt56_IsByteIdenticalPassthrough()
    {
        const string delta = "\r\nstill mine";

        CopilotTextDeltaNormalizer.Normalize("gpt-5", delta).ShouldBe(delta);
    }

    // Bare LF is how the model emits genuine Markdown structure. If this ever regresses, every
    // paragraph, list and fenced code block in gpt-5.6 output collapses - the exact user-visible
    // damage the normalizer exists to avoid causing.
    [Fact]
    public void Normalize_Gpt56_BareLfMarkdownStructure_IsPreservedVerbatim()
    {
        const string delta = "\n\n- one\n- two\n\n```\ncode\n```\n";

        CopilotTextDeltaNormalizer.Normalize("gpt-5.6", delta).ShouldBe(delta);
    }

    // Only a LEADING prefix is transport framing. A CRLF in the middle is content.
    [Fact]
    public void Normalize_Gpt56_InteriorCrlf_IsNotStripped()
        => CopilotTextDeltaNormalizer.Normalize("gpt-5.6", "Hello\r\nWorld").ShouldBe("Hello\r\nWorld");

    [Fact]
    public void Normalize_Gpt56_CrlfAfterLeadingPrefix_SurvivesInBody()
        => CopilotTextDeltaNormalizer.Normalize("gpt-5.6", "\r\nA\r\nB").ShouldBe("A\r\nB");

    [Fact]
    public void Normalize_Gpt56_LoneCarriageReturn_IsNotStripped()
        => CopilotTextDeltaNormalizer.Normalize("gpt-5.6", "\rHello").ShouldBe("\rHello");

    [Fact]
    public void Normalize_Gpt56_LeadingLfCrlf_IsNotStripped()
        => CopilotTextDeltaNormalizer.Normalize("gpt-5.6", "\n\r\nHello").ShouldBe("\n\r\nHello");

    [Fact]
    public void Normalize_Gpt56_CrlfOnlyDelta_CollapsesToEmpty()
        => CopilotTextDeltaNormalizer.Normalize("gpt-5.6", "\r\n\r\n").ShouldBe("");

    [Fact]
    public void Normalize_EmptyDelta_IsUnchanged()
        => CopilotTextDeltaNormalizer.Normalize("gpt-5.6", "").ShouldBe("");

    // Mid-word split: chunk boundaries are transport metadata and land anywhere, including inside a
    // word. Concatenating normalized deltas must still reproduce the word exactly.
    [Fact]
    public void Normalize_Gpt56_MidWordSplitDeltas_ConcatenateToOriginalWord()
    {
        var assembled =
            CopilotTextDeltaNormalizer.Normalize("gpt-5.6", "stre") +
            CopilotTextDeltaNormalizer.Normalize("gpt-5.6", "aming");

        assembled.ShouldBe("streaming");
    }

    // Hit counter ------------------------------------------------------------
    // The counter is process-global mutable state, so its tests are serialized against each other
    // via a dedicated non-parallel collection. Without that, a sibling test incrementing the same
    // static between the baseline read and the assertion produces an intermittent red that has
    // nothing to do with the behaviour under test.

    [Collection(nameof(CopilotTextDeltaNormalizerCounterCollection))]
    public class HitCounter
    {
        [Fact]
        public void Normalize_WhenItStrips_IncrementsHitCounter()
        {
            var before = CopilotTextDeltaNormalizer.HitCount;

            CopilotTextDeltaNormalizer.Normalize("gpt-5.6", "\r\nfires");

            CopilotTextDeltaNormalizer.HitCount.ShouldBe(before + 1);
        }

        // The counter is the whole point of the falsifiability argument: if it can move on clean
        // traffic it cannot answer "does this ever actually fire in production?".
        [Fact]
        public void Normalize_WhenItDoesNotStrip_LeavesHitCounterUnchanged()
        {
            var before = CopilotTextDeltaNormalizer.HitCount;

            CopilotTextDeltaNormalizer.Normalize("gpt-5.6", "clean text");
            CopilotTextDeltaNormalizer.Normalize("claude-sonnet-4", "\r\nuntouched");

            CopilotTextDeltaNormalizer.HitCount.ShouldBe(before);
        }
    }
}

/// <summary>
/// Serializes the hit-counter tests, which observe process-global mutable state (#2443).
/// </summary>
[CollectionDefinition(nameof(CopilotTextDeltaNormalizerCounterCollection), DisableParallelization = true)]
public class CopilotTextDeltaNormalizerCounterCollection;

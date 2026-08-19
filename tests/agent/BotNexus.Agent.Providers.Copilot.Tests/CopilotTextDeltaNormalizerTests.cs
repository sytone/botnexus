using BotNexus.Agent.Providers.Copilot;

namespace BotNexus.Agent.Providers.Copilot.Tests;

/// <summary>
/// Direct, falsifiable coverage of <see cref="CopilotTextDeltaNormalizer"/> (#2443, #3336).
/// </summary>
/// <remarks>
/// Before this file the normalizer had zero direct tests: it was exercised only incidentally by two
/// parity tests that synthesise <c>"\r\n" + fragment</c>, i.e. tests that assume the very wire shape
/// under question. A content-mutating transform needs its no-fire cases pinned at least as hard as
/// its fire cases, because the expensive failure mode is silently deleting legitimate model content,
/// not failing to strip framing.
/// <para>
/// #3336 replaced the <c>modelId.StartsWith("gpt-5.6")</c> gate with a transport-declared flag, so
/// the discriminator these tests exercise is now a boolean the provider declares rather than a
/// model-id spelling. The model-family cases below became flag cases: the behaviour they pinned
/// (a non-Copilot transport is a byte-identical passthrough) is preserved, with the correct
/// discriminator.
/// </para>
/// </remarks>
public class CopilotTextDeltaNormalizerTests
{
    private const bool Framed = true;
    private const bool NotFramed = false;

    // Fire cases -------------------------------------------------------------

    [Fact]
    public void Normalize_FramedTransport_SingleLeadingCrlf_IsStripped()
        => CopilotTextDeltaNormalizer.Normalize(Framed, "\r\nHello").ShouldBe("Hello");

    [Fact]
    public void Normalize_FramedTransport_RepeatedLeadingCrlf_AllPairsStripped()
        => CopilotTextDeltaNormalizer.Normalize(Framed, "\r\n\r\n\r\nHello").ShouldBe("Hello");

    // #3336: the strip is no longer confined to one model family. A CRLF-framed delta on a
    // claude-shaped model over the same transport is the reported defect, and it must be stripped.
    [Fact]
    public void Normalize_FramedTransport_AppliesRegardlessOfModelFamily()
        => CopilotTextDeltaNormalizer.Normalize(Framed, "\r\nclaude text").ShouldBe("claude text");

    // No-fire cases - the ones that matter most --------------------------------

    // A transport that does not declare the quirk keeps every byte, which is the whole point of
    // declaring it: a provider does not pay for another provider's defect (#2432).
    [Fact]
    public void Normalize_UndeclaredTransport_IsByteIdenticalPassthrough()
    {
        const string delta = "\r\n\r\nanother provider keeps its bytes";

        CopilotTextDeltaNormalizer.Normalize(NotFramed, delta).ShouldBe(delta);
    }

    [Fact]
    public void Normalize_UndeclaredTransport_SingleLeadingCrlf_IsByteIdenticalPassthrough()
    {
        const string delta = "\r\nstill mine";

        CopilotTextDeltaNormalizer.Normalize(NotFramed, delta).ShouldBe(delta);
    }

    // Bare LF is how the model emits genuine Markdown structure. If this ever regresses, every
    // paragraph, list and fenced code block collapses - the exact user-visible damage the
    // normalizer exists to avoid causing.
    [Fact]
    public void Normalize_FramedTransport_BareLfMarkdownStructure_IsPreservedVerbatim()
    {
        const string delta = "\n\n- one\n- two\n\n```\ncode\n```\n";

        CopilotTextDeltaNormalizer.Normalize(Framed, delta).ShouldBe(delta);
    }

    // Only a LEADING prefix is transport framing. A CRLF in the middle is content.
    [Fact]
    public void Normalize_FramedTransport_InteriorCrlf_IsNotStripped()
        => CopilotTextDeltaNormalizer.Normalize(Framed, "Hello\r\nWorld").ShouldBe("Hello\r\nWorld");

    [Fact]
    public void Normalize_FramedTransport_CrlfAfterLeadingPrefix_SurvivesInBody()
        => CopilotTextDeltaNormalizer.Normalize(Framed, "\r\nA\r\nB").ShouldBe("A\r\nB");

    [Fact]
    public void Normalize_FramedTransport_LoneCarriageReturn_IsNotStripped()
        => CopilotTextDeltaNormalizer.Normalize(Framed, "\rHello").ShouldBe("\rHello");

    [Fact]
    public void Normalize_FramedTransport_LeadingLfCrlf_IsNotStripped()
        => CopilotTextDeltaNormalizer.Normalize(Framed, "\n\r\nHello").ShouldBe("\n\r\nHello");

    [Fact]
    public void Normalize_FramedTransport_CrlfOnlyDelta_CollapsesToEmpty()
        => CopilotTextDeltaNormalizer.Normalize(Framed, "\r\n\r\n").ShouldBe("");

    [Fact]
    public void Normalize_EmptyDelta_IsUnchanged()
        => CopilotTextDeltaNormalizer.Normalize(Framed, "").ShouldBe("");

    // Mid-word split: chunk boundaries are transport metadata and land anywhere, including inside a
    // word. Concatenating normalized deltas must still reproduce the word exactly.
    [Fact]
    public void Normalize_FramedTransport_MidWordSplitDeltas_ConcatenateToOriginalWord()
    {
        var assembled =
            CopilotTextDeltaNormalizer.Normalize(Framed, "stre") +
            CopilotTextDeltaNormalizer.Normalize(Framed, "aming");

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

            CopilotTextDeltaNormalizer.Normalize(Framed, "\r\nfires");

            CopilotTextDeltaNormalizer.HitCount.ShouldBe(before + 1);
        }

        // The counter is the whole point of the falsifiability argument: if it can move on clean
        // traffic it cannot answer "does this ever actually fire in production?".
        [Fact]
        public void Normalize_WhenItDoesNotStrip_LeavesHitCounterUnchanged()
        {
            var before = CopilotTextDeltaNormalizer.HitCount;

            CopilotTextDeltaNormalizer.Normalize(Framed, "clean text");
            CopilotTextDeltaNormalizer.Normalize(NotFramed, "\r\nuntouched");

            CopilotTextDeltaNormalizer.HitCount.ShouldBe(before);
        }
    }
}

/// <summary>
/// Serializes the hit-counter tests, which observe process-global mutable state (#2443).
/// </summary>
[CollectionDefinition(nameof(CopilotTextDeltaNormalizerCounterCollection), DisableParallelization = true)]
public class CopilotTextDeltaNormalizerCounterCollection;

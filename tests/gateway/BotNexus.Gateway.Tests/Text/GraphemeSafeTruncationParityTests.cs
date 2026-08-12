using System.Text;

using BotNexus.Domain.Text;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.Telegram;

using Shouldly;

namespace BotNexus.Gateway.Tests.Text;

/// <summary>
/// #2924 acceptance criterion 3: the portal path, the Telegram path and the domain path must all
/// apply the SAME grapheme-cluster boundary policy, so a preview truncated in any of them cuts at
/// the same place and never emits a dangling ZWJ, a severed flag pair or an orphaned combining mark.
/// </summary>
/// <remarks>
/// <para>
/// This test class lives in <c>BotNexus.Gateway.Tests</c> because it is the only test project that
/// references all three production assemblies at once - the domain, the Telegram channel and the
/// Blazor client. Asserting parity in three separate suites would let them drift again silently,
/// which is exactly the failure #2924 was filed for; the three implementations each had their own
/// green test suite while disagreeing with each other.
/// </para>
/// <para>
/// <b>These assertions would have FAILED before the unification.</b> For the 8-code-unit ZWJ family
/// sequence truncated to 4 units, the portal and Telegram implementations returned the first four
/// units - a complete U+1F468 followed by a dangling U+200D - while the domain returned empty.
/// </para>
/// </remarks>
public sealed class GraphemeSafeTruncationParityTests
{
    /// <summary>U+1F468 ZWJ U+1F469 ZWJ U+1F467 - one grapheme cluster, 8 UTF-16 code units.</summary>
    private const string FamilyZwj = "\U0001F468\u200D\U0001F469\u200D\U0001F467";

    /// <summary>Regional indicators U+1F1EC U+1F1E7 - the GB flag, one cluster, 4 code units.</summary>
    private const string FlagGb = "\U0001F1EC\U0001F1E7";

    /// <summary>"e" + U+0301 COMBINING ACUTE ACCENT - one cluster, 2 code units.</summary>
    private const string CombiningEAcute = "e\u0301";

    private const char Zwj = '\u200D';

    [Fact]
    public void TestPremises_HoldSoTheAssertionsBelowAreMeaningful()
    {
        // If these drift the tests below could pass without exercising a mid-cluster cut at all.
        FamilyZwj.Length.ShouldBe(8);
        FlagGb.Length.ShouldBe(4);
        CombiningEAcute.Length.ShouldBe(2);
        FamilyZwj[2].ShouldBe(Zwj);
    }

    /// <summary>
    /// The headline clause: cutting the ZWJ family sequence mid-cluster yields no trailing ZWJ and
    /// no lone surrogate, on all three paths.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void ZwjSequence_CutMidCluster_LeavesNoDanglingJoinerOnAnyPath(int limit)
    {
        var padded = "ab" + FamilyZwj;

        AssertWellFormed(Portal(padded, limit), $"portal @ {limit}");
        AssertWellFormed(Telegram(padded, limit), $"telegram @ {limit}");
        AssertWellFormed(Domain(padded, limit), $"domain @ {limit}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FlagPair_CutMidCluster_IsNeverSeveredOnAnyPath(int limit)
    {
        // A lone regional indicator renders as a bare letter tile instead of a flag.
        AssertWellFormed(Portal(FlagGb, limit), $"portal @ {limit}");
        AssertWellFormed(Telegram(FlagGb, limit), $"telegram @ {limit}");
        AssertWellFormed(Domain(FlagGb, limit), $"domain @ {limit}");
    }

    [Fact]
    public void CombiningMark_IsNeverOrphanedFromItsBaseOnAnyPath()
    {
        // Limit 1 lands between "e" and the combining acute; keeping only "e" is correct, but
        // keeping the mark without its base (or vice versa across a chunk) is not.
        var value = "xy" + CombiningEAcute + "z";

        Portal(value, 3).ShouldBe("xy");
        Domain(value, 3).ShouldBe("xy");
        Telegram(value, 3).ShouldBe("xy");
    }

    /// <summary>
    /// Parity is asserted as an identity, not three separate expectations: over every cut point of
    /// a string mixing ASCII, astral emoji, a flag, a combining mark and a ZWJ sequence, all three
    /// paths must return the SAME retained prefix. A per-path expectation could be updated on one
    /// path alone and let them diverge again.
    /// </summary>
    [Fact]
    public void AllThreePaths_AgreeOnEveryCutPoint()
    {
        var value = "ab\U0001F600" + FlagGb + CombiningEAcute + FamilyZwj + "cd";

        for (var limit = 0; limit <= value.Length + 2; limit++)
        {
            var domain = Domain(value, limit);
            var portal = Portal(value, limit);

            portal.ShouldBe(
                domain,
                $"#2924: portal and domain must cut identically at limit {limit}.");

            // The chunking path guarantees forward progress, so it may return a shorter-than-cluster
            // slice ONLY when not even one cluster fits (domain returns empty). Everywhere else it
            // must agree exactly.
            var telegram = Telegram(value, limit);
            if (limit > 0 && domain.Length == 0)
            {
                telegram.Length.ShouldBeGreaterThan(
                    0,
                    $"#2924: the chunking path must always advance at limit {limit}.");
                AssertNoLoneSurrogates(telegram, $"telegram forward-progress slice @ {limit}");
            }
            else
            {
                telegram.ShouldBe(
                    domain,
                    $"#2924: telegram and domain must cut identically at limit {limit}.");
            }
        }
    }

    /// <summary>
    /// #2924 criterion 6 / #2883 parity: a value within the limit is returned unchanged (and by
    /// reference on the domain path), and the suffix is appended only when truncation occurred.
    /// </summary>
    [Fact]
    public void ShortValue_IsReturnedUnchanged_AndSuffixAppearsOnlyOnTruncation()
    {
        var value = "hello " + FamilyZwj;

        ReferenceEquals(value, TextTruncation.SafeTruncate(value, value.Length, "...")).ShouldBeTrue(
            "#2883: an untruncated value must be returned by reference, not copied.");
        ReferenceEquals(value, TextTruncation.SafeTruncate(value, value.Length + 100, "...")).ShouldBeTrue();

        TextTruncation.SafeTruncate(value, value.Length, "...")!.ShouldNotContain("...");
        TextTruncation.SafeTruncate(value, 5, "...").ShouldBe("hello...");

        SurrogateSafeText.SurrogateSafeTruncate(value, value.Length).ShouldBe(value);
        SurrogateSafeText.SurrogateSafeTruncate(null, 10).ShouldBe(string.Empty);
        SurrogateSafeText.SurrogateSafeTruncate(string.Empty, 10).ShouldBe(string.Empty);
        SurrogateSafeText.SurrogateSafeTruncate("abc", 0).ShouldBe(string.Empty);
    }

    /// <summary>
    /// The chunking path must never stall, even when a single cluster is wider than the limit -
    /// splitting a Telegram message would loop forever otherwise. Forward progress outranks cluster
    /// integrity in that degenerate case, but the surrogate-pair floor is still never breached.
    /// </summary>
    [Fact]
    public void ChunkingPath_AlwaysAdvances_EvenWhenAClusterExceedsTheLimit()
    {
        foreach (var maxLength in new[] { 1, 2, 3 })
        {
            var offset = 0;
            var content = FamilyZwj + FlagGb + "tail";
            var guard = 0;

            while (offset < content.Length)
            {
                var chunk = TelegramMessageSplitter.SliceSurrogateSafe(content, offset, maxLength);
                chunk.Length.ShouldBeGreaterThan(
                    0,
                    $"#2924: chunking must advance at maxLength {maxLength}, offset {offset}.");
                offset += chunk.Length;

                (++guard).ShouldBeLessThan(1000, "chunking loop failed to terminate");
            }

            offset.ShouldBe(content.Length);
        }
    }

    /// <summary>
    /// The streaming drain shares the same policy, and reassembly stays lossless.
    /// </summary>
    [Fact]
    public void StreamingDrain_UsesTheSamePolicy_AndIsLossless()
    {
        var content = "aaa" + FamilyZwj + FlagGb + "bbb";
        var buffer = new StringBuilder(content);
        var chunks = new List<string>();

        while (buffer.Length > 5)
        {
            var before = buffer.Length;
            var chunk = TelegramMessageSplitter.DrainStreamingBuffer(buffer, 5);
            buffer.Length.ShouldBeLessThan(before, "each drain must make forward progress");
            AssertNoLoneSurrogates(chunk, "drained chunk");
            chunks.Add(chunk);
        }

        (string.Concat(chunks) + buffer).ShouldBe(content);
    }

    // ---- the three production paths under test ----

    private static string Portal(string value, int limit)
        => SurrogateSafeText.SurrogateSafeTruncate(value, limit);

    private static string Telegram(string value, int limit)
        => limit <= 0 ? string.Empty : TelegramMessageSplitter.SliceSurrogateSafe(value, 0, limit);

    private static string Domain(string value, int limit)
        => TextTruncation.SafeTruncate(value, limit, string.Empty) ?? string.Empty;

    private static void AssertWellFormed(string result, string because)
    {
        AssertNoLoneSurrogates(result, because);

        if (result.Length > 0)
        {
            result[^1].ShouldNotBe(
                Zwj,
                $"#2924: {because} left a dangling zero-width joiner - the cluster was severed.");
        }

        result.ShouldNotContain(
            '\uFFFD',
            $"#2924: {because} produced U+FFFD.");
    }

    private static void AssertNoLoneSurrogates(string result, string because)
    {
        for (var i = 0; i < result.Length; i++)
        {
            if (char.IsHighSurrogate(result[i]))
            {
                (i + 1 < result.Length && char.IsLowSurrogate(result[i + 1])).ShouldBeTrue(
                    $"#2924: {because} left a lone high surrogate at index {i}.");
                i++;
            }
            else
            {
                char.IsLowSurrogate(result[i]).ShouldBeFalse(
                    $"#2924: {because} left a lone low surrogate at index {i}.");
            }
        }
    }
}

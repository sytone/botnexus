using BotNexus.Domain.Text;
using System.Text;

namespace BotNexus.Domain.Tests;

/// <summary>
/// Covers <see cref="TextTruncation.SafeTruncate"/>, the single helper introduced by #2883 to stop
/// raw <c>value[..max]</c> slicing from splitting surrogate pairs in user- and model-supplied text.
/// </summary>
public class TextTruncationTests
{
    private const string Grinning = "\U0001F600";      // 2 UTF-16 code units
    private const string FamilyZwj = "\U0001F468\u200D\U0001F469\u200D\U0001F466"; // 8 code units

    /// <summary>
    /// Acceptance criterion 1: a cut whose limit lands on a high surrogate must not leave a lone
    /// surrogate behind. This is the defect the issue was filed for.
    /// </summary>
    [Fact]
    public void SafeTruncate_CutInsideSurrogatePair_DoesNotEmitLoneSurrogate()
    {
        var value = string.Concat(Enumerable.Repeat(Grinning, 10));

        // A limit of 5 lands between the two halves of the third emoji, whose pair occupies
        // indices 4 and 5, so a raw slice would retain the high surrogate and drop its low half.
        Assert.True(char.IsHighSurrogate(value[4]), "test premise: index 4 must be a high surrogate");
        Assert.True(char.IsLowSurrogate(value[5]), "test premise: index 5 must be a low surrogate");

        var result = TextTruncation.SafeTruncate(value, 5, string.Empty);

        Assert.NotNull(result);
        Assert.Equal(4, result!.Length);
        AssertNoLoneSurrogates(result);
    }

    /// <summary>
    /// Acceptance criterion 1, exhaustively: no cut point over an all-astral string may produce a
    /// lone surrogate. A single hand-picked index could pass by luck; sweeping every index cannot.
    /// </summary>
    [Fact]
    public void SafeTruncate_EveryCutPointOverAstralText_IsWellFormed()
    {
        var value = string.Concat(Enumerable.Repeat(Grinning, 10));

        for (var limit = 0; limit <= value.Length + 2; limit++)
        {
            var result = TextTruncation.SafeTruncate(value, limit, string.Empty);

            Assert.NotNull(result);
            AssertNoLoneSurrogates(result!);
            Assert.True(result!.Length % 2 == 0, $"limit {limit} produced an odd length");
        }
    }

    /// <summary>
    /// A boundary that already falls between two complete characters must be honoured exactly, so
    /// the helper is not needlessly lossy compared with the slicing it replaces.
    /// </summary>
    [Fact]
    public void SafeTruncate_LimitOnPairBoundary_KeepsExactlyThatMuch()
    {
        var value = string.Concat(Enumerable.Repeat(Grinning, 10));

        var result = TextTruncation.SafeTruncate(value, 6, string.Empty);

        Assert.Equal(6, result!.Length);
        Assert.Equal(string.Concat(Enumerable.Repeat(Grinning, 3)), result);
    }

    /// <summary>
    /// Acceptance criterion 4: ASCII behaviour must be byte-identical to the raw slicing this
    /// replaces, so no existing display output regresses.
    /// </summary>
    [Theory]
    [InlineData("hello world this is plain", 11, "...", "hello world...")]
    [InlineData("hello", 5, "...", "hello")]
    [InlineData("hello", 99, "...", "hello")]
    [InlineData("", 5, "...", "")]
    public void SafeTruncate_AsciiInput_MatchesRawSlicingBehaviour(
        string value, int maxLength, string suffix, string expected)
    {
        Assert.Equal(expected, TextTruncation.SafeTruncate(value, maxLength, suffix));
    }

    /// <summary>
    /// The no-truncation path must not allocate a copy; callers rely on this being as cheap as the
    /// length check it replaces.
    /// </summary>
    [Fact]
    public void SafeTruncate_ShorterThanLimit_ReturnsSameReference()
    {
        var value = "unchanged";

        Assert.Same(value, TextTruncation.SafeTruncate(value, 100, "..."));
    }

    [Fact]
    public void SafeTruncate_Null_ReturnsNull()
    {
        Assert.Null(TextTruncation.SafeTruncate(null, 10, "..."));
    }

    /// <summary>
    /// The suffix marks elision, so appending it to text that was never shortened would be a lie
    /// and would also push short values over the caller's intended width.
    /// </summary>
    [Fact]
    public void SafeTruncate_SuffixAppliedOnlyWhenTruncated()
    {
        Assert.Equal("abc", TextTruncation.SafeTruncate("abc", 10, "..."));
        Assert.Equal("ab...", TextTruncation.SafeTruncate("abcdef", 2, "..."));
    }

    /// <summary>
    /// Grapheme awareness: a ZWJ emoji sequence is one perceived character spanning 8 code units.
    /// Cutting inside it yields visually broken output even though every surrogate pair is intact,
    /// so the helper must drop the whole cluster.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(7)]
    public void SafeTruncate_InsideZwjCluster_DropsWholeCluster(int limit)
    {
        var result = TextTruncation.SafeTruncate(FamilyZwj, limit, string.Empty);

        Assert.Equal(string.Empty, result);
    }

    /// <summary>
    /// A combining mark must never be orphaned from its base character; on its own it renders as a
    /// stray accent against whatever follows.
    /// </summary>
    [Fact]
    public void SafeTruncate_CombiningMark_StaysWithBaseCharacter()
    {
        const string value = "e\u0301abc"; // e + combining acute, then ASCII

        Assert.Equal(string.Empty, TextTruncation.SafeTruncate(value, 1, string.Empty));
        Assert.Equal("e\u0301", TextTruncation.SafeTruncate(value, 2, string.Empty));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void SafeTruncate_NonPositiveLimit_YieldsSuffixOnly(int limit)
    {
        Assert.Equal("...", TextTruncation.SafeTruncate("abcdef", limit, "..."));
    }

    /// <summary>
    /// Acceptance criterion 1's round-trip half: the result must survive UTF-8 encoding, which is
    /// what JSON, SQLite and SignalR all do to it. A lone surrogate is not encodable and silently
    /// becomes U+FFFD, which is precisely the corruption that cannot be repaired once persisted.
    /// </summary>
    [Fact]
    public void SafeTruncate_ResultRoundTripsThroughUtf8WithoutReplacementCharacters()
    {
        var value = string.Concat(Enumerable.Repeat(Grinning, 30));

        for (var limit = 1; limit < value.Length; limit++)
        {
            var result = TextTruncation.SafeTruncate(value, limit, string.Empty)!;

            var roundTripped = Encoding.UTF8.GetString(
                Encoding.UTF8.GetBytes(result));

            Assert.Equal(result, roundTripped);
            Assert.DoesNotContain('\uFFFD', roundTripped);
        }
    }

    /// <summary>
    /// Demonstrates the defect being fixed, so the tests fail loudly if someone reverts a call site
    /// to raw slicing: the naive expression really does corrupt this input.
    /// </summary>
    [Fact]
    public void RawSlicing_IsActuallyBroken_ForTheSameInput()
    {
        var value = string.Concat(Enumerable.Repeat(Grinning, 10));

        var naive = value[..5];
        var encoded = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(naive));

        Assert.Contains('\uFFFD', encoded);
        Assert.DoesNotContain('\uFFFD', Encoding.UTF8.GetString(
            Encoding.UTF8.GetBytes(TextTruncation.SafeTruncate(value, 5, string.Empty)!)));
    }

    private static void AssertNoLoneSurrogates(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                Assert.True(
                    i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]),
                    $"lone high surrogate at index {i} of \"{value}\"");
                i++;
                continue;
            }

            Assert.False(
                char.IsLowSurrogate(value[i]),
                $"lone low surrogate at index {i} of \"{value}\"");
        }
    }
}

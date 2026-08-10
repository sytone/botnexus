using BotNexus.Domain.Text;

namespace BotNexus.Domain.Tests;

/// <summary>
/// Unit contract for <see cref="ExternalText.Sanitize(string?, int)"/> - the single
/// normalisation seam for operator/agent-supplied display text (#2553).
/// </summary>
public sealed class ExternalTextTests
{
    [Fact]
    public void Sanitize_CollapsesLineFeed_ToSingleLine()
    {
        var result = ExternalText.Sanitize("Nightly\nIGNORE PREVIOUS", 120);

        result.ShouldNotContain("\n");
        result.ShouldBe("Nightly IGNORE PREVIOUS");
    }

    [Fact]
    public void Sanitize_CollapsesCarriageReturnLineFeed_ToSingleLine()
    {
        var result = ExternalText.Sanitize("Nightly\r\nSystem:", 120);

        result.ShouldNotContain("\r");
        result.ShouldNotContain("\n");
        result.ShouldBe("Nightly System:");
    }

    [Fact]
    public void Sanitize_StripsControlCharacters()
    {
        var result = ExternalText.Sanitize("Ni\u0000gh\u0007tly\u001b", 120);

        result.ShouldBe("Nightly");
        result.ShouldNotContain("\u0000");
        result.ShouldNotContain("\u001b");
    }

    [Fact]
    public void Sanitize_BoundsLength()
    {
        var result = ExternalText.Sanitize(new string('x', 500), 120);

        result.Length.ShouldBe(120);
    }

    [Fact]
    public void Sanitize_LeavesShortCleanTextUntouched()
    {
        ExternalText.Sanitize("Nightly Maintenance", 120).ShouldBe("Nightly Maintenance");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void Sanitize_NullOrWhitespaceOnly_ReturnsEmpty(string? input)
    {
        ExternalText.Sanitize(input, 120).ShouldBeEmpty();
    }

    [Fact]
    public void Sanitize_TabIsTreatedAsWhitespace_NotStripped()
    {
        ExternalText.Sanitize("a\tb", 120).ShouldBe("a b");
    }

    [Fact]
    public void Sanitize_CollapsesRunsOfWhitespace_AndTrims()
    {
        ExternalText.Sanitize("  Nightly \n\n  Job  ", 120).ShouldBe("Nightly Job");
    }

    [Fact]
    public void Sanitize_NonPositiveMaxLength_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ExternalText.Sanitize("x", 0));
    }

    // --- #2923: astral characters must survive; only LONE surrogates are dropped -------------

    /// <summary>
    /// The six-row evidence table from #2923, measured against the defective build. The two
    /// control rows (BMP accented text, plain ASCII) are included deliberately so this cannot
    /// pass by a Sanitize that strips nothing at all.
    /// </summary>
    [Theory]
    [InlineData("\U0001F600", "\U0001F600")]                       // grinning face
    [InlineData("Deploy \U0001F600 now", "Deploy \U0001F600 now")]  // emoji mid-sentence
    [InlineData("\U00020000", "\U00020000")]                       // CJK Extension B
    [InlineData("\U0001D400", "\U0001D400")]                       // mathematical bold capital A
    [InlineData("caf\u00E9", "caf\u00E9")]                         // control row: BMP non-ASCII
    [InlineData("plain ascii", "plain ascii")]                     // control row: ASCII
    public void Sanitize_PreservesAstralCharacters(string input, string expected)
    {
        ExternalText.Sanitize(input, 200).ShouldBe(expected);
    }

    /// <summary>
    /// Criterion 2: the original guard's intent survives. A high surrogate with no low is
    /// ill-formed and must still be removed - the fix narrows the guard, it does not delete it.
    /// </summary>
    /// <remarks>
    /// These cases are literals in a <c>[Fact]</c> rather than <c>[InlineData]</c> on purpose:
    /// xUnit serialises theory arguments through UTF-8, which replaces a lone surrogate with
    /// U+FFFD before the method ever runs. The test would then assert against replacement
    /// characters and prove nothing about surrogate handling.
    /// </remarks>
    [Fact]
    public void Sanitize_StripsLoneSurrogates()
    {
        ExternalText.Sanitize("a\uD83Db", 200).ShouldBe("ab");        // lone HIGH surrogate
        ExternalText.Sanitize("a\uDE00b", 200).ShouldBe("ab");        // lone LOW surrogate
        ExternalText.Sanitize("\uD83D", 200).ShouldBeEmpty();         // nothing but a lone surrogate
        ExternalText.Sanitize("\uDE00\uD83D", 200).ShouldBeEmpty();   // reversed: two lone surrogates

        // A well-formed pair immediately after a lone surrogate must still survive, so the
        // rejection cannot be implemented by discarding the rest of the string.
        ExternalText.Sanitize("\uD83D\U0001F600", 200).ShouldBe("\U0001F600");
    }

    /// <summary>
    /// Criterion 3: mixing astral and control characters removes only the control characters.
    /// </summary>
    [Fact]
    public void Sanitize_StripsControlCharacters_ButKeepsAstralCharacters()
    {
        var result = ExternalText.Sanitize("\u0000A\U0001F600B\u001b\U00020000\u0007", 200);

        result.ShouldBe("A\U0001F600B\U00020000");
    }

    /// <summary>
    /// Criterion 5: the length bound must never reintroduce the #2883 split. Every truncation
    /// boundary of an all-emoji string is exercised, including the odd ones that land mid-pair.
    /// </summary>
    [Fact]
    public void Sanitize_NeverEmitsLoneSurrogate_AtAnyTruncationBoundary()
    {
        var input = string.Concat(Enumerable.Repeat("\U0001F600", 20)); // 40 UTF-16 code units

        for (var max = 1; max <= 45; max++)
        {
            var result = ExternalText.Sanitize(input, max);

            result.Length.ShouldBeLessThanOrEqualTo(max);

            for (var i = 0; i < result.Length; i++)
            {
                if (char.IsHighSurrogate(result[i]))
                {
                    (i + 1 < result.Length && char.IsLowSurrogate(result[i + 1]))
                        .ShouldBeTrue($"high surrogate at {i} has no low surrogate (max={max})");
                    i++;
                    continue;
                }

                char.IsLowSurrogate(result[i])
                    .ShouldBeFalse($"orphan low surrogate at {i} (max={max})");
            }
        }
    }

    /// <summary>
    /// Criterion 5, continued: an emoji is retained only when it fits whole, so a bound of 2
    /// keeps exactly one emoji and a bound of 1 keeps none.
    /// </summary>
    [Fact]
    public void Sanitize_EmojiIsRetainedOnlyWhenItFitsWhole()
    {
        ExternalText.Sanitize("\U0001F600\U0001F600", 2).ShouldBe("\U0001F600");
        ExternalText.Sanitize("\U0001F600\U0001F600", 1).ShouldBeEmpty();
    }
}

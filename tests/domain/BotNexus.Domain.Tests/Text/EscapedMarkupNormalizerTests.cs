using System.Diagnostics;
using System.Text.RegularExpressions;
using BotNexus.Domain.Text;

namespace BotNexus.Domain.Tests.Text;

/// <summary>
/// Tests for <see cref="EscapedMarkupNormalizer"/> - the single decode-then-scan seam that lets
/// marker patterns be written once, in literal form, yet still catch escaped spellings of the
/// same marker (issue #2808).
/// </summary>
public class EscapedMarkupNormalizerTests
{
    private static readonly Regex SpecialToken = new(@"<\|[^|>\r\n]*\|>", RegexOptions.IgnoreCase);

    [Fact]
    public void LiteralMarker_IsRemoved()
    {
        EscapedMarkupNormalizer.ReplaceMatches("a<|im_start|>b", SpecialToken).ShouldBe("ab");
    }

    [Fact]
    public void UnicodeEscapedMarker_IsRemovedFromTheOriginalSpan()
    {
        EscapedMarkupNormalizer.ReplaceMatches(@"a\u003c|im_start|\u003eb", SpecialToken)
            .ShouldBe("ab");
    }

    [Fact]
    public void HexEscapedMarker_IsRemovedFromTheOriginalSpan()
    {
        EscapedMarkupNormalizer.ReplaceMatches(@"a\x3c|im_start|\x3eb", SpecialToken).ShouldBe("ab");
    }

    [Fact]
    public void HtmlEntityMarker_IsRemovedFromTheOriginalSpan()
    {
        EscapedMarkupNormalizer.ReplaceMatches("a&lt;|im_start|&gt;b", SpecialToken).ShouldBe("ab");
    }

    [Fact]
    public void NumericEntityMarker_IsRemovedFromTheOriginalSpan()
    {
        EscapedMarkupNormalizer.ReplaceMatches("a&#60;|im_start|&#x3E;b", SpecialToken).ShouldBe("ab");
    }

    [Fact]
    public void NonMatchingText_ReturnsSameReference()
    {
        const string input = @"prose with \u003c and &lt; and nothing marker-shaped";
        ReferenceEquals(EscapedMarkupNormalizer.ReplaceMatches(input, SpecialToken), input)
            .ShouldBeTrue();
    }

    [Fact]
    public void DecodingDoesNotRewriteSurvivingText()
    {
        // Only the matched span is removed; escapes elsewhere keep their original spelling.
        EscapedMarkupNormalizer.ReplaceMatches(@"keep \u003cthis\u003e drop <|x|> keep &lt;too", SpecialToken)
            .ShouldBe(@"keep \u003cthis\u003e drop  keep &lt;too");
    }

    [Fact]
    public void MalformedEscapes_AreTreatedAsLiteralCharacters()
    {
        const string input = @"\u00 \uZZZZ \x \xZZ &lt &#; &notanentity;";
        EscapedMarkupNormalizer.ReplaceMatches(input, SpecialToken).ShouldBe(input);
    }

    [Fact]
    public void LargeAdversarialInput_CompletesWithinBoundedTime()
    {
        var input = string.Concat(Enumerable.Repeat(@"\u003c|", 100 * 1024 / 7));
        var sw = Stopwatch.StartNew();
        _ = EscapedMarkupNormalizer.ReplaceMatches(input, SpecialToken);
        sw.Stop();
        sw.ElapsedMilliseconds.ShouldBeLessThan(2000);
    }
}

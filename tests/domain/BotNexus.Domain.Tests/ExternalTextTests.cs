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
}

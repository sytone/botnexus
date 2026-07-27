using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #2441: single-line normalisation is the structural guarantee that user-supplied agent names,
/// descriptions and conversation titles cannot grow a chrome row or defeat ellipsis truncation.
/// </summary>
public sealed class PortalTextTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("a", "a")]
    [InlineData("plain title", "plain title")]
    [InlineData("  padded  ", "padded")]
    public void SingleLine_handles_trivial_and_empty_input(string? input, string expected) =>
        Assert.Equal(expected, PortalText.SingleLine(input));

    [Theory]
    [InlineData("a\nb", "a b")]
    [InlineData("a\r\nb", "a b")]
    [InlineData("a\tb", "a b")]
    [InlineData("a\n\n\n\tb", "a b")]
    [InlineData("\na\n", "a")]
    [InlineData("a\u0000b", "a b")]
    [InlineData("a\u000Bb", "a b")]
    public void SingleLine_collapses_control_characters_to_single_spaces(string input, string expected) =>
        Assert.Equal(expected, PortalText.SingleLine(input));

    [Fact]
    public void SingleLine_never_emits_control_characters()
    {
        var result = PortalText.SingleLine("mix\r\n\ttext\u0007more");
        Assert.DoesNotContain(result, c => char.IsControl(c));
    }

    [Fact]
    public void SingleLine_preserves_multi_codepoint_zwj_sequences()
    {
        // ZWJ (U+200D) and variation selectors are format characters, not whitespace, so an
        // emoji family sequence must round-trip unchanged.
        const string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466";
        Assert.Equal(family, PortalText.SingleLine(family));
    }

    [Fact]
    public void SingleLine_preserves_combining_marks()
    {
        const string combining = "e\u0301\u0327";
        Assert.Equal(combining, PortalText.SingleLine(combining));
    }

    [Fact]
    public void SingleLine_preserves_length_of_a_long_single_line_value()
    {
        var input = new string('X', 300);
        Assert.Equal(300, PortalText.SingleLine(input).Length);
    }
}

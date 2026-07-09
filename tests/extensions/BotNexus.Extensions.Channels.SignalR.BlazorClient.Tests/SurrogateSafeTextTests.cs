using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for <see cref="SurrogateSafeText"/>, the shared portal-preview truncation helper.
/// </summary>
public sealed class SurrogateSafeTextTests
{
    // U+1F600 GRINNING FACE encodes as the surrogate pair D83D DE00 (two UTF-16 code units).
    private const string Emoji = "\U0001F600";

    [Fact]
    public void Short_value_is_returned_unchanged()
    {
        Assert.Equal("hello", SurrogateSafeText.SurrogateSafeTruncate("hello", 50));
    }

    [Fact]
    public void Null_returns_empty()
    {
        Assert.Equal(string.Empty, SurrogateSafeText.SurrogateSafeTruncate(null, 10));
    }

    [Fact]
    public void Empty_returns_empty()
    {
        Assert.Equal(string.Empty, SurrogateSafeText.SurrogateSafeTruncate(string.Empty, 10));
    }

    [Fact]
    public void Exact_length_is_returned_unchanged()
    {
        Assert.Equal("abcde", SurrogateSafeText.SurrogateSafeTruncate("abcde", 5));
    }

    [Fact]
    public void Plain_text_is_truncated_to_max()
    {
        Assert.Equal("abc", SurrogateSafeText.SurrogateSafeTruncate("abcdef", 3));
    }

    [Fact]
    public void Emoji_straddling_the_limit_is_dropped_whole_not_severed()
    {
        // "ab" + emoji (2 code units). Limit 3 would cut mid-pair -> lone high surrogate.
        var input = "ab" + Emoji + "cd";
        var result = SurrogateSafeText.SurrogateSafeTruncate(input, 3);

        Assert.Equal("ab", result);
        Assert.DoesNotContain('\uFFFD', result);
        Assert.False(char.IsHighSurrogate(result[^1]), "Result must not end on a lone high surrogate.");
    }

    [Fact]
    public void Emoji_fully_inside_the_limit_is_kept()
    {
        // "ab" + emoji occupies code units 0..3; limit 4 keeps the whole pair.
        var input = "ab" + Emoji + "cd";
        var result = SurrogateSafeText.SurrogateSafeTruncate(input, 4);

        Assert.Equal("ab" + Emoji, result);
        Assert.DoesNotContain('\uFFFD', result);
    }

    [Fact]
    public void Non_positive_max_returns_empty()
    {
        Assert.Equal(string.Empty, SurrogateSafeText.SurrogateSafeTruncate("abc", 0));
    }
}

using BotNexus.Agent.Providers.Core.Utilities;
using FluentAssertions;

namespace BotNexus.Providers.Core.Tests.Utilities;

public class UnicodeSanitizerTests
{
    [Fact]
    public void NormalText_PassesThrough()
    {
        var result = UnicodeSanitizer.SanitizeSurrogates("Hello, world!");

        result.Should().Be("Hello, world!");
    }

    [Fact]
    public void UnpairedHighSurrogate_Removed()
    {
        // \uD800 is a high surrogate without a following low surrogate
        var input = "before\uD800after";
        var result = UnicodeSanitizer.SanitizeSurrogates(input);

        result.Should().NotContain("\uFFFD");
        result.Should().NotContain("\uD800");
        result.Should().Be("beforeafter");
    }

    [Fact]
    public void UnpairedLowSurrogate_Removed()
    {
        // \uDC00 is a low surrogate without a preceding high surrogate
        var input = "before\uDC00after";
        var result = UnicodeSanitizer.SanitizeSurrogates(input);

        result.Should().NotContain("\uFFFD");
        result.Should().Be("beforeafter");
    }

    [Fact]
    public void ValidSurrogatePair_Preserved()
    {
        // \uD83D\uDE00 = 😀
        var input = "smile \uD83D\uDE00 emoji";
        var result = UnicodeSanitizer.SanitizeSurrogates(input);

        result.Should().Be(input);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        UnicodeSanitizer.SanitizeSurrogates("").Should().Be("");
    }

    [Fact]
    public void NullInput_ReturnsNull()
    {
        UnicodeSanitizer.SanitizeSurrogates(null!).Should().BeNull();
    }
}

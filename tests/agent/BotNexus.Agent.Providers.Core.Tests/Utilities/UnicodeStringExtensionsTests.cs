using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Core.Tests.Utilities;

public class UnicodeStringExtensionsTests
{
    [Fact]
    public void NormalText_PassesThrough()
    {
        var result = ("Hello, world!").SanitizeSurrogates();

        result.ShouldBe("Hello, world!");
    }

    [Fact]
    public void UnpairedHighSurrogate_Removed()
    {
        // \uD800 is a high surrogate without a following low surrogate
        var input = "before\uD800after";
        var result = input.SanitizeSurrogates();

        result.ShouldNotContain("\uFFFD");
        result.ShouldNotContain("\uD800");
        result.ShouldBe("beforeafter");
    }

    [Fact]
    public void UnpairedLowSurrogate_Removed()
    {
        // \uDC00 is a low surrogate without a preceding high surrogate
        var input = "before\uDC00after";
        var result = input.SanitizeSurrogates();

        result.ShouldNotContain("\uFFFD");
        result.ShouldBe("beforeafter");
    }

    [Fact]
    public void ValidSurrogatePair_Preserved()
    {
        // \uD83D\uDE00 = 😀
        var input = "smile \uD83D\uDE00 emoji";
        var result = input.SanitizeSurrogates();

        result.ShouldBe(input);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        ("").SanitizeSurrogates().ShouldBe("");
    }

    [Fact]
    public void NullInput_ReturnsNull()
    {
        ((string)null!).SanitizeSurrogates().ShouldBeNull();
    }
}


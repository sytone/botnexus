using System.Text.RegularExpressions;
using BotNexus.Agent.Providers.Core.Utilities;
using FluentAssertions;

namespace BotNexus.Providers.Core.Tests.Utilities;

public class ShortHashTests
{
    [Fact]
    public void Generate_WithSameInput_ReturnsDeterministicHash()
    {
        var first = ShortHash.Generate("toolu_abc123");
        var second = ShortHash.Generate("toolu_abc123");

        first.Should().Be(second);
    }

    [Fact]
    public void Generate_ReturnsExpectedLength()
    {
        var hash = ShortHash.Generate("toolu_abc123");

        // pi-mono's shortHash concatenates two base-36 uint32 representations,
        // producing variable-length strings typically 12-14 characters.
        hash.Length.Should().BeInRange(6, 14);
    }

    [Fact]
    public void Generate_ReturnsOnlyAlphanumericCharacters()
    {
        var hash = ShortHash.Generate("toolu_abc123");

        Regex.IsMatch(hash, "^[a-zA-Z0-9]+$").Should().BeTrue();
    }
}

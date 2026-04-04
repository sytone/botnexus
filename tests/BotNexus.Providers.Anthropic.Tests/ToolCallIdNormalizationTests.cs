using System.Text.RegularExpressions;
using FluentAssertions;

namespace BotNexus.Providers.Anthropic.Tests;

public class ToolCallIdNormalizationTests
{
    // Mirrors the NormalizeToolCallId logic from AnthropicProvider
    private static readonly Regex NonAlphanumericRegex = new("[^a-zA-Z0-9_-]");

    private static string NormalizeToolCallId(string id)
    {
        var normalized = NonAlphanumericRegex.Replace(id, "");
        if (normalized.Length > 64)
            normalized = normalized[..64];
        return normalized;
    }

    [Fact]
    public void SpecialCharacters_Normalized()
    {
        var result = NormalizeToolCallId("toolu_01!@#$%^&*()");

        result.Should().Be("toolu_01");
    }

    [Fact]
    public void LongId_TruncatedTo64Characters()
    {
        var longId = new string('a', 100);
        var result = NormalizeToolCallId(longId);

        result.Should().HaveLength(64);
    }

    [Fact]
    public void AlphanumericId_Unchanged()
    {
        var result = NormalizeToolCallId("toolu_01Abc-def_123");

        result.Should().Be("toolu_01Abc-def_123");
    }

    [Fact]
    public void EmptyId_ReturnsEmpty()
    {
        var result = NormalizeToolCallId("");

        result.Should().BeEmpty();
    }

    [Fact]
    public void DotsAndSpaces_Removed()
    {
        var result = NormalizeToolCallId("tool.call id:v2");

        result.Should().Be("toolcallidv2");
    }
}

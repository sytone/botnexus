using System.Text.RegularExpressions;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

public class ToolCallIdNormalizationTests
{
    // Mirrors the NormalizeToolCallId logic from AnthropicProvider
    private static readonly Regex NonAlphanumericRegex = new("[^a-zA-Z0-9_-]");

    private static string NormalizeToolCallId(string id)
    {
        var normalized = NonAlphanumericRegex.Replace(id, "_");
        if (normalized.Length > 64)
            normalized = normalized[..64];
        return normalized;
    }

    [Fact]
    public void SpecialCharacters_Normalized()
    {
        var result = NormalizeToolCallId("toolu_01!@#$%^&*()");

        result.ShouldBe("toolu_01__________");
    }

    [Fact]
    public void NormalizeToolCallId_LongId_TruncatedTo64Characters()
    {
        var longId = new string('a', 100);
        var result = NormalizeToolCallId(longId);

        result.Length.ShouldBe(64);
    }

    [Fact]
    public void AlphanumericId_Unchanged()
    {
        var result = NormalizeToolCallId("toolu_01Abc-def_123");

        result.ShouldBe("toolu_01Abc-def_123");
    }

    [Fact]
    public void EmptyId_ReturnsEmpty()
    {
        var result = NormalizeToolCallId("");

        result.ShouldBeEmpty();
    }

    [Fact]
    public void NormalizeToolCallId_DotsAndSpaces_ReplacedWithUnderscores()
    {
        var result = NormalizeToolCallId("tool.call id:v2");

        result.ShouldBe("tool_call_id_v2");
    }

    [Fact]
    public void NormalizeToolCallId_PipeCharacter_ReplacedWithUnderscore()
    {
        var result = NormalizeToolCallId("call_abc|fc_xyz");

        result.ShouldBe("call_abc_fc_xyz");
    }

    [Fact]
    public void NormalizeToolCallId_WithReplacement_TruncatesTo64Characters()
    {
        var id = $"call_{new string('a', 70)}|fc_xyz";
        var expected = id.Replace("|", "_", StringComparison.Ordinal)[..64];

        NormalizeToolCallId(id).ShouldBe(expected);
    }
}

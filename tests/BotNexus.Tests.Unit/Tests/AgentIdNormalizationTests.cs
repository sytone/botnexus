using FluentAssertions;
using System.Text.RegularExpressions;
using Xunit;

namespace BotNexus.Tests.Unit.Tests;

public class AgentIdNormalizationTests
{
    [Theory]
    [InlineData("Nova Star", "nova-star")]
    [InlineData("Hello World", "hello-world")]
    [InlineData("Test Agent 123", "test-agent-123")]
    [InlineData("my_agent", "my-agent")]
    [InlineData("Agent!@#$%Name", "agent-name")]
    [InlineData("  Trimmed  ", "trimmed")]
    [InlineData("Multiple   Spaces", "multiple-spaces")]
    [InlineData("dash--collapse", "dash-collapse")]
    [InlineData("-leading-trailing-", "leading-trailing")]
    [InlineData("CamelCaseAgent", "camelcaseagent")]
    [InlineData("UPPERCASE", "uppercase")]
    [InlineData("mixed123CASE456", "mixed123case456")]
    [InlineData("special!@#chars$%^", "special-chars")]
    [InlineData("under_score_name", "under-score-name")]
    [InlineData("dot.separated.name", "dot-separated-name")]
    [InlineData("emoji😀agent", "emoji-agent")]
    [InlineData("unicode™®©", "unicode")]
    [InlineData("a", "a")]
    [InlineData("123", "123")]
    [InlineData("a-b-c", "a-b-c")]
    public void NormalizeAgentId_TransformsCorrectly(string input, string expected)
    {
        var result = NormalizeAgentId(input);
        result.Should().Be(expected);
    }

    [Fact]
    public void NormalizeAgentId_EmptyString_ReturnsEmpty()
    {
        var result = NormalizeAgentId("");
        result.Should().BeEmpty();
    }

    [Fact]
    public void NormalizeAgentId_WhitespaceOnly_ReturnsEmpty()
    {
        var result = NormalizeAgentId("   ");
        result.Should().BeEmpty();
    }

    [Fact]
    public void NormalizeAgentId_NullString_ReturnsEmpty()
    {
        var result = NormalizeAgentId(null!);
        result.Should().BeEmpty();
    }

    [Fact]
    public void NormalizeAgentId_SpecialCharsOnly_ResultsInEmpty()
    {
        var result = NormalizeAgentId("!@#$%^&*()");
        result.Should().BeEmpty();
    }

    [Fact]
    public void NormalizeAgentId_PreservesNumbers()
    {
        var result = NormalizeAgentId("Agent 007");
        result.Should().Be("agent-007");
    }

    [Fact]
    public void NormalizeAgentId_MultipleConsecutiveSpecialChars_CollapseToSingleDash()
    {
        var result = NormalizeAgentId("test!!!agent");
        result.Should().Be("test-agent");
    }

    [Fact]
    public void NormalizeAgentId_MixedCase_ConvertedToLowercase()
    {
        var result = NormalizeAgentId("MyAgentName");
        result.Should().Be("myagentname");
    }

    [Fact]
    public void NormalizeAgentId_AlreadyNormalized_RemainsUnchanged()
    {
        var result = NormalizeAgentId("my-agent-123");
        result.Should().Be("my-agent-123");
    }

    [Fact]
    public void NormalizeAgentId_LeadingAndTrailingSpecialChars_AreRemoved()
    {
        var result = NormalizeAgentId("!!!agent!!!");
        result.Should().Be("agent");
    }

    [Fact]
    public void NormalizeAgentId_Idempotent_MultipleApplicationsSameResult()
    {
        var input = "Nova Star Agent!";
        var firstPass = NormalizeAgentId(input);
        var secondPass = NormalizeAgentId(firstPass);

        firstPass.Should().Be(secondPass);
    }

    private static string NormalizeAgentId(string agentName)
    {
        if (string.IsNullOrWhiteSpace(agentName))
            return string.Empty;

        var normalized = agentName.Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^a-z0-9]+", "-");
        normalized = normalized.Trim('-');
        normalized = Regex.Replace(normalized, @"-+", "-");

        return normalized;
    }
}

using BotNexus.CodingAgent.Cli;
using BotNexus.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.CodingAgent.Tests.Cli;

public sealed class CommandParserTests
{
    private readonly CommandParser _parser = new();

    [Theory]
    [InlineData("minimal", ThinkingLevel.Minimal)]
    [InlineData("low", ThinkingLevel.Low)]
    [InlineData("medium", ThinkingLevel.Medium)]
    [InlineData("high", ThinkingLevel.High)]
    [InlineData("xhigh", ThinkingLevel.ExtraHigh)]
    public void Parse_WithValidThinkingLevel_ParsesLevel(string level, ThinkingLevel expected)
    {
        var options = _parser.Parse(["--thinking", level]);

        options.ThinkingSpecified.Should().BeTrue();
        options.ThinkingLevel.Should().Be(expected);
    }

    [Fact]
    public void Parse_WithThinkingOff_ParsesNull()
    {
        var options = _parser.Parse(["--thinking", "off"]);

        options.ThinkingSpecified.Should().BeTrue();
        options.ThinkingLevel.Should().BeNull();
    }

    [Fact]
    public void Parse_WithoutThinking_LeavesDefault()
    {
        var options = _parser.Parse([]);

        options.ThinkingSpecified.Should().BeFalse();
        options.ThinkingLevel.Should().BeNull();
    }

    [Fact]
    public void Parse_WithInvalidThinking_Throws()
    {
        var action = () => _parser.Parse(["--thinking", "turbo"]);

        action.Should().Throw<ArgumentException>()
            .WithMessage("Invalid value for --thinking*");
    }
}

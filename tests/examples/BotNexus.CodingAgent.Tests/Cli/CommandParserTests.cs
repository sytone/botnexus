using BotNexus.CodingAgent.Cli;
using BotNexus.Agent.Providers.Core.Models;

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

        options.ThinkingSpecified.ShouldBeTrue();
        options.ThinkingLevel.ShouldBe(expected);
    }

    [Fact]
    public void Parse_WithThinkingOff_ParsesNull()
    {
        var options = _parser.Parse(["--thinking", "off"]);

        options.ThinkingSpecified.ShouldBeTrue();
        options.ThinkingLevel.ShouldBeNull();
    }

    [Fact]
    public void Parse_WithoutThinking_LeavesDefault()
    {
        var options = _parser.Parse([]);

        options.ThinkingSpecified.ShouldBeFalse();
        options.ThinkingLevel.ShouldBeNull();
    }

    [Fact]
    public void Parse_WithInvalidThinking_Throws()
    {
        var action = () => _parser.Parse(["--thinking", "turbo"]);

        action.ShouldThrow<ArgumentException>()
            .Message.ShouldStartWith("Invalid value for --thinking");
    }
}

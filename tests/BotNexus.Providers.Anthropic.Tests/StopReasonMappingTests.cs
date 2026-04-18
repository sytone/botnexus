using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.Providers.Anthropic.Tests;

public class StopReasonMappingTests
{
    [Theory]
    [InlineData("stop", StopReason.Stop)]
    [InlineData("length", StopReason.Length)]
    [InlineData("toolUse", StopReason.ToolUse)]
    [InlineData("error", StopReason.Error)]
    [InlineData("aborted", StopReason.Aborted)]
    public void StopReason_JsonDeserialization_MapsCorrectly(string jsonValue, StopReason expected)
    {
        var json = $"\"{jsonValue}\"";
        var result = JsonSerializer.Deserialize<StopReason>(json);

        result.Should().Be(expected);
    }

    [Fact]
    public void StopReason_Stop_SerializesToStop()
    {
        var json = JsonSerializer.Serialize(StopReason.Stop);

        json.Should().Be("\"stop\"");
    }

    [Fact]
    public void StopReason_Length_SerializesToLength()
    {
        var json = JsonSerializer.Serialize(StopReason.Length);

        json.Should().Be("\"length\"");
    }

    [Fact]
    public void StopReason_ToolUse_SerializesToToolUse()
    {
        var json = JsonSerializer.Serialize(StopReason.ToolUse);

        json.Should().Be("\"toolUse\"");
    }
}

using System.Text.Json;
using BotNexus.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.Providers.OpenAI.Tests;

public class StopReasonMappingTests
{
    [Theory]
    [InlineData("stop", StopReason.Stop)]
    [InlineData("length", StopReason.Length)]
    [InlineData("toolUse", StopReason.ToolUse)]
    [InlineData("error", StopReason.Error)]
    [InlineData("aborted", StopReason.Aborted)]
    public void StopReason_JsonSerialization_MapsCorrectly(string jsonValue, StopReason expected)
    {
        var json = $"\"{jsonValue}\"";
        var result = JsonSerializer.Deserialize<StopReason>(json);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(StopReason.Stop, "stop")]
    [InlineData(StopReason.Length, "length")]
    [InlineData(StopReason.ToolUse, "toolUse")]
    [InlineData(StopReason.Error, "error")]
    public void StopReason_JsonDeserialization_ProducesCorrectString(StopReason value, string expectedJson)
    {
        var json = JsonSerializer.Serialize(value);

        json.Should().Be($"\"{expectedJson}\"");
    }
}

using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

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

        result.ShouldBe(expected);
    }

    [Fact]
    public void StopReason_Stop_SerializesToStop()
    {
        var json = JsonSerializer.Serialize(StopReason.Stop);

        json.ShouldBe("\"stop\"");
    }

    [Fact]
    public void StopReason_Length_SerializesToLength()
    {
        var json = JsonSerializer.Serialize(StopReason.Length);

        json.ShouldBe("\"length\"");
    }

    [Fact]
    public void StopReason_ToolUse_SerializesToToolUse()
    {
        var json = JsonSerializer.Serialize(StopReason.ToolUse);

        json.ShouldBe("\"toolUse\"");
    }
}

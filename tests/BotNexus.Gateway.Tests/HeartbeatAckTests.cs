using System.Reflection;
using BotNexus.Gateway;
using FluentAssertions;

namespace BotNexus.Gateway.Tests;

public sealed class HeartbeatAckTests
{
    [Theory]
    [InlineData("HEARTBEAT_OK", true)]
    [InlineData("  HEARTBEAT_OK", true)]
    [InlineData("HEARTBEAT_OK - nothing to do", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Here is the task summary...", false)]
    [InlineData("heartbeat_ok", false)]
    public void IsHeartbeatAck_ReturnsExpectedResult(string? response, bool expected)
    {
        var result = InvokeIsHeartbeatAck(response);

        result.Should().Be(expected);
    }

    private static bool InvokeIsHeartbeatAck(string? response)
    {
        var method = typeof(GatewayHost).GetMethod("IsHeartbeatAck", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        return (bool)method!.Invoke(null, [response])!;
    }
}

using System.Reflection;
using BotNexus.Gateway;

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

        result.ShouldBe(expected);
    }

    private static bool InvokeIsHeartbeatAck(string? response)
    {
        var method = typeof(GatewayHost).GetMethod("IsHeartbeatAck", BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();

        return (bool)method!.Invoke(null, [response])!;
    }
}

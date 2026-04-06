using BotNexus.Gateway.Configuration;
using FluentAssertions;

namespace BotNexus.Gateway.Tests;

public sealed class GatewayOptionsTests
{
    [Fact]
    public void DefaultAgentId_CanBeNull()
    {
        var options = new GatewayOptions();

        options.DefaultAgentId.Should().BeNull();
    }

    [Fact]
    public void DefaultAgentId_CanBeAssigned()
    {
        var options = new GatewayOptions
        {
            DefaultAgentId = "agent-a"
        };

        options.DefaultAgentId.Should().Be("agent-a");
    }

    [Fact]
    public void MaxCallChainDepth_DefaultsToTen()
    {
        var options = new GatewayOptions();

        options.MaxCallChainDepth.Should().Be(10);
    }

    [Fact]
    public void CrossAgentTimeoutSeconds_DefaultsToOneHundredTwenty()
    {
        var options = new GatewayOptions();

        options.CrossAgentTimeoutSeconds.Should().Be(120);
    }
}

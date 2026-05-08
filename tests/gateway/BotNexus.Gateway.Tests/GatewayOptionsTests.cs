using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests;

public sealed class GatewayOptionsTests
{
    [Fact]
    public void DefaultAgentId_CanBeNull()
    {
        var options = new GatewayOptions();

        options.DefaultAgentId.ShouldBeNull();
    }

    [Fact]
    public void DefaultAgentId_CanBeAssigned()
    {
        var options = new GatewayOptions
        {
            DefaultAgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };

        options.DefaultAgentId.ShouldBe("agent-a");
    }

    [Fact]
    public void MaxCallChainDepth_DefaultsToTen()
    {
        var options = new GatewayOptions();

        options.MaxCallChainDepth.ShouldBe(10);
    }

    [Fact]
    public void CrossAgentTimeoutSeconds_DefaultsToOneHundredTwenty()
    {
        var options = new GatewayOptions();

        options.CrossAgentTimeoutSeconds.ShouldBe(120);
    }
}

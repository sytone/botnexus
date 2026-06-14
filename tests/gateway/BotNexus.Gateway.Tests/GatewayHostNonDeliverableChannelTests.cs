using BotNexus.Domain.Primitives;
using Xunit;

namespace BotNexus.Gateway.Tests;

public sealed class GatewayHostNonDeliverableChannelTests
{
    [Theory]
    [InlineData("cron", true)]
    [InlineData("Cron", true)]
    [InlineData("CRON", true)]
    [InlineData("exchange", true)]
    [InlineData("Exchange", true)]
    [InlineData("signalr", false)]
    [InlineData("telegram", false)]
    [InlineData("signal", false)]
    public void IsNonDeliverableChannel_ClassifiesCorrectly(string channelType, bool expected)
    {
        var key = ChannelKey.From(channelType);
        Assert.Equal(expected, GatewayHost.IsNonDeliverableChannel(key));
    }

    [Fact]
    public void NonDeliverableChannels_ContainsCron()
    {
        Assert.Contains("cron", GatewayHost.NonDeliverableChannels);
    }

    [Fact]
    public void NonDeliverableChannels_ContainsExchange()
    {
        Assert.Contains("exchange", GatewayHost.NonDeliverableChannels);
    }
}

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
    [InlineData("webhook", true)]
    [InlineData("Webhook", true)]
    [InlineData("api", false)]
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

    /// <summary>
    /// #3541 clause 6: channel type <c>api</c> is deliberately NOT in the non-deliverable set.
    /// <c>ConversationMessagesController</c> stamps <c>api</c> on messages it accepts, and the
    /// caller's stated contract is to poll history for the reply - but nothing in source declares
    /// api undeliverable the way <c>WebhookResponseMode</c> does for webhook, so silencing it would
    /// hide a genuine binding misconfiguration behind a design claim nobody has made.
    /// </summary>
    [Fact]
    public void NonDeliverableChannels_DoesNotContainApi()
    {
        Assert.DoesNotContain("api", GatewayHost.NonDeliverableChannels);
    }
}

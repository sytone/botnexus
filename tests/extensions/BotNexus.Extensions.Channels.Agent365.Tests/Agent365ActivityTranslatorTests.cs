using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Extensions.Channels.Agent365;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Agents.Core.Models;

namespace BotNexus.Extensions.Channels.Agent365.Tests;

/// <summary>
/// Unit tests for the pure Activity &lt;-&gt; Inbound/OutboundMessage translation, the core testable
/// logic of the Agent 365 adapter (Register tier, PBI #1876).
/// </summary>
public sealed class Agent365ActivityTranslatorTests
{
    [Fact]
    public void ToInboundMessage_MessageActivity_RoundTripsCoreFields()
    {
        var activity = new Activity
        {
            Type = ActivityTypes.Message,
            Id = "activity-1",
            Text = "hello from m365",
            ServiceUrl = "https://smba.example.com/amer/",
            From = new ChannelAccount { Id = "user-42", Name = "Ada" },
            Recipient = new ChannelAccount { Id = "bot-1" },
            Conversation = new ConversationAccount { Id = "conv-99" },
        };

        var inbound = Agent365ActivityTranslator.ToInboundMessage(activity, targetAgentId: "farnsworth");

        Assert.NotNull(inbound);
        Assert.Equal(ChannelKey.From("agent365"), inbound!.ChannelType);
        Assert.Equal("user-42", inbound.SenderId);
        Assert.Equal(CitizenId.Of(UserId.From("user-42")), inbound.Sender);
        Assert.Equal("hello from m365", inbound.Content);

        // Conversation id + serviceUrl are folded into the channel address for the reply path.
        Assert.True(Agent365ChannelAddress.TryDecode(inbound.ChannelAddress, out var conv, out var svc));
        Assert.Equal("conv-99", conv);
        Assert.Equal("https://smba.example.com/amer/", svc);

        Assert.NotNull(inbound.RoutingHints);
        Assert.Equal("farnsworth", inbound.RoutingHints!.RequestedAgentId?.Value);
        Assert.Equal("activity-1", inbound.Metadata["agent365ActivityId"]);
    }

    [Fact]
    public void ToInboundMessage_ImageAttachment_ProducesReferenceContentPart()
    {
        var activity = new Activity
        {
            Type = ActivityTypes.Message,
            Text = string.Empty,
            From = new ChannelAccount { Id = "user-1" },
            Conversation = new ConversationAccount { Id = "conv-1" },
            Attachments =
            [
                new Attachment { ContentType = "image/png", ContentUrl = "https://cdn.example.com/a.png", Name = "a.png" },
                new Attachment { ContentType = "application/pdf", ContentUrl = "https://cdn.example.com/doc.pdf" },
            ],
        };

        var inbound = Agent365ActivityTranslator.ToInboundMessage(activity);

        Assert.NotNull(inbound);
        var part = Assert.Single(inbound!.ContentParts!);
        var reference = Assert.IsType<ReferenceContentPart>(part);
        Assert.Equal("image/png", reference.MimeType);
        Assert.Equal("https://cdn.example.com/a.png", reference.Uri);
        Assert.Equal("a.png", reference.FileName);
    }

    [Fact]
    public void ToInboundMessage_NonMessageActivity_ReturnsNull()
    {
        var activity = new Activity
        {
            Type = ActivityTypes.ConversationUpdate,
            From = new ChannelAccount { Id = "user-1" },
            Conversation = new ConversationAccount { Id = "conv-1" },
        };

        Assert.Null(Agent365ActivityTranslator.ToInboundMessage(activity));
    }

    [Fact]
    public void ToInboundMessage_EmptyMessageNoAttachments_ReturnsNull()
    {
        var activity = new Activity
        {
            Type = ActivityTypes.Message,
            Text = "   ",
            From = new ChannelAccount { Id = "user-1" },
            Conversation = new ConversationAccount { Id = "conv-1" },
        };

        Assert.Null(Agent365ActivityTranslator.ToInboundMessage(activity));
    }

    [Fact]
    public void ToReplyActivity_BuildsMessageActivityWithConversationAndServiceUrl()
    {
        var message = new OutboundMessage
        {
            ChannelType = ChannelKey.From("agent365"),
            ChannelAddress = Agent365ChannelAddress.Encode("conv-77", "https://smba.example.com/emea/"),
            Content = "the answer is 42",
        };

        var activity = Agent365ActivityTranslator.ToReplyActivity(message, replyToId: "activity-9");

        Assert.Equal(ActivityTypes.Message, activity.Type);
        Assert.Equal("the answer is 42", activity.Text);
        Assert.Equal("activity-9", activity.ReplyToId);
        Assert.Equal("https://smba.example.com/emea/", activity.ServiceUrl);
        Assert.NotNull(activity.Conversation);
        Assert.Equal("conv-77", activity.Conversation!.Id);
    }

    [Fact]
    public void ToReplyActivity_AppliesDisplayPrefix()
    {
        var message = new OutboundMessage
        {
            ChannelType = ChannelKey.From("agent365"),
            ChannelAddress = Agent365ChannelAddress.Encode("conv-1", null),
            Content = "body",
            DisplayPrefix = "[farnsworth]",
        };

        var activity = Agent365ActivityTranslator.ToReplyActivity(message);

        Assert.Equal("[farnsworth] body", activity.Text);
        // No serviceUrl was encoded, so the reply carries none (the connector falls back to config).
        Assert.Null(activity.ServiceUrl);
    }

    [Theory]
    [InlineData("conv-1", "https://svc.example.com/x/")]
    [InlineData("conv-2", null)]
    public void ChannelAddress_RoundTrips(string conversationId, string? serviceUrl)
    {
        var address = Agent365ChannelAddress.Encode(conversationId, serviceUrl);

        Assert.True(Agent365ChannelAddress.TryDecode(address, out var conv, out var svc));
        Assert.Equal(conversationId, conv);
        Assert.Equal(serviceUrl, svc);
    }
}

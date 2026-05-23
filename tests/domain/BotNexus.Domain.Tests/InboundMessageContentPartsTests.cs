using System.Runtime.CompilerServices;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Domain.Tests;

public sealed class InboundMessageContentPartsTests
{
    [Fact]
    public void InboundMessage_Constructor_WithoutContentParts_ShouldDefaultToNull()
    {
        var message = CreateMessage();

        message.ContentParts.ShouldBeNull();
    }

    [Fact]
    public void InboundMessage_Constructor_WithEmptyContentParts_ShouldSucceed()
    {
        var message = CreateMessage() with { ContentParts = [] };

        message.ContentParts.ShouldNotBeNull();
        message.ContentParts.ShouldBeEmpty();
    }

    [Fact]
    public void InboundMessage_Constructor_WithMixedContentParts_ShouldPreserveValues()
    {
        var text = new TextContentPart { MimeType = "text/plain", Text = "hello" };
        var binary = new BinaryContentPart { MimeType = "application/octet-stream", Data = [1, 2] };
        var reference = new ReferenceContentPart { MimeType = "image/png", Uri = "https://example.invalid/image.png" };
        var message = CreateMessage() with { ContentParts = [text, binary, reference] };

        message.ContentParts.ShouldNotBeNull();
        message.ContentParts.Count().ShouldBe(3);
        message.ContentParts![0].ShouldBeSameAs(text);
        message.ContentParts[1].ShouldBeSameAs(binary);
        message.ContentParts[2].ShouldBeSameAs(reference);
    }

    [Fact]
    public void InboundMessage_ContentProperty_ShouldBeRequiredEvenWhenContentPartsIsSet()
    {
        var contentProperty = typeof(InboundMessage).GetProperty(nameof(InboundMessage.Content));

        contentProperty.ShouldNotBeNull();
        contentProperty!.GetCustomAttributes(typeof(RequiredMemberAttribute), inherit: false)
            .Length.ShouldBe(1);
    }

    [Fact]
    public void InboundMessage_ContentParts_WhenSpecified_ShouldPreserveOrder()
    {
        var first = new TextContentPart { MimeType = "text/plain", Text = "first" };
        var second = new TextContentPart { MimeType = "text/plain", Text = "second" };
        var third = new TextContentPart { MimeType = "text/plain", Text = "third" };
        var message = CreateMessage() with { ContentParts = [first, second, third] };

        message.ContentParts.ShouldNotBeNull();
        message.ContentParts!.ToList().ShouldBe(new[] { first, second, third });
    }

    private static InboundMessage CreateMessage() => new()
    {
        ChannelType = ChannelKey.From("signalr"),
        SenderId = "sender-1",
        Sender = CitizenId.Of(UserId.From("sender-1")),
        ChannelAddress = ChannelAddress.From("conversation-1"),
        Content = "hello"
    };
}

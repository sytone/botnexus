using System.Collections.Generic;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Domain.Tests;

/// <summary>
/// Covers the <c>clientKind</c> entry that SignalR (and any other transport that can
/// distinguish device classes) stamps into <see cref="InboundMessage.Metadata"/> so the
/// connecting client type (e.g. "mobile" vs "desktop") reaches the agent runtime (#1209,
/// AC#3). The domain layer owns no behaviour here -- <see cref="InboundMessage.Metadata"/>
/// is already an extensible dictionary -- so these tests pin the additive contract: the
/// key round-trips through construction and the record's <c>with</c> copy, and the absence
/// of the key is the backward-compatible default (AC#5).
/// </summary>
public sealed class InboundMessageClientKindTests
{
    private const string ClientKindKey = "clientKind";

    [Fact]
    public void InboundMessage_WithoutClientKind_MetadataDoesNotContainKey()
    {
        var message = CreateMessage();

        message.Metadata.ContainsKey(ClientKindKey).ShouldBeFalse();
    }

    [Fact]
    public void InboundMessage_WithClientKindMetadata_ExposesValue()
    {
        var message = CreateMessage() with
        {
            Metadata = new Dictionary<string, object?>
            {
                ["messageType"] = "message",
                [ClientKindKey] = "mobile"
            }
        };

        message.Metadata.ContainsKey(ClientKindKey).ShouldBeTrue();
        message.Metadata[ClientKindKey].ShouldBe("mobile");
        // The pre-existing messageType entry must survive alongside the new key.
        message.Metadata["messageType"].ShouldBe("message");
    }

    [Fact]
    public void InboundMessage_ClientKindMetadata_SurvivesRecordWithCopy()
    {
        var original = CreateMessage() with
        {
            Metadata = new Dictionary<string, object?> { [ClientKindKey] = "desktop" }
        };

        var copy = original with { Content = "changed" };

        copy.Metadata[ClientKindKey].ShouldBe("desktop");
        copy.Content.ShouldBe("changed");
    }

    private static InboundMessage CreateMessage() => new()
    {
        ChannelType = ChannelKey.From("signalr"),
        SenderId = "sender-1",
        Sender = CitizenId.Of(UserId.From("sender-1")),
        ChannelAddress = ChannelAddress.From("conversation-1"),
        Content = "hello",
    };
}

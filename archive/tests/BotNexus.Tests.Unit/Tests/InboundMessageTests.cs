using BotNexus.Core.Models;
using FluentAssertions;
using Xunit;

namespace BotNexus.Tests.Unit.Tests;

public class InboundMessageTests
{
    [Fact]
    public void SessionKey_UsesChannelAndChatId_WhenNoOverride()
    {
        var message = new InboundMessage("telegram", "user1", "chat1", "hello",
            DateTimeOffset.UtcNow, [], new Dictionary<string, object>());

        message.SessionKey.Should().Be("telegram:chat1");
    }

    [Fact]
    public void SessionKey_UsesOverride_WhenProvided()
    {
        var message = new InboundMessage("telegram", "user1", "chat1", "hello",
            DateTimeOffset.UtcNow, [], new Dictionary<string, object>(),
            SessionKeyOverride: "custom-key");

        message.SessionKey.Should().Be("custom-key");
    }

    [Fact]
    public void SessionKey_DifferentChannels_ProduceDifferentKeys()
    {
        var telegram = new InboundMessage("telegram", "user1", "chat1", "hello",
            DateTimeOffset.UtcNow, [], new Dictionary<string, object>());
        var discord = new InboundMessage("discord", "user1", "chat1", "hello",
            DateTimeOffset.UtcNow, [], new Dictionary<string, object>());

        telegram.SessionKey.Should().NotBe(discord.SessionKey);
    }

    [Fact]
    public void Record_SameValues_ProduceEqualSessionKeys()
    {
        var ts = DateTimeOffset.UtcNow;
        var m1 = new InboundMessage("telegram", "u", "c", "hello", ts, [], new Dictionary<string, object>());
        var m2 = new InboundMessage("telegram", "u", "c", "hello", ts, [], new Dictionary<string, object>());

        m1.SessionKey.Should().Be(m2.SessionKey);
        m1.Channel.Should().Be(m2.Channel);
    }
}

using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.Telegram;
using Shouldly;

namespace BotNexus.Gateway.Tests.Channels;

public sealed class TelegramChannelAddressTests
{
    [Fact]
    public void Encode_WithoutTopic_ReturnsBareChatId()
    {
        var address = TelegramChannelAddress.Encode(12345, null);
        address.Value.ShouldBe("12345");
    }

    [Fact]
    public void Encode_WithTopic_AppendsTopicSegment()
    {
        var address = TelegramChannelAddress.Encode(12345, 67);
        address.Value.ShouldBe("12345/topic:67");
    }

    [Fact]
    public void Encode_GeneralTopic_AppendsTopic1()
    {
        var address = TelegramChannelAddress.Encode(12345, 1);
        address.Value.ShouldBe("12345/topic:1");
    }

    [Fact]
    public void Encode_SupergroupChatId_PreservesNegativeSign()
    {
        var address = TelegramChannelAddress.Encode(-1001234567890, 5);
        address.Value.ShouldBe("-1001234567890/topic:5");
    }

    [Theory]
    [InlineData("12345", 12345L, null)]
    [InlineData("12345/topic:67", 12345L, 67)]
    [InlineData("12345/topic:1", 12345L, 1)]
    [InlineData("-1001234567890/topic:5", -1001234567890L, 5)]
    [InlineData("-1001234567890", -1001234567890L, null)]
    public void TryDecode_ValidShapes_RoundTrip(string raw, long expectedChatId, int? expectedTopic)
    {
        var ok = TelegramChannelAddress.TryDecode(ChannelAddress.From(raw), out var chatId, out var topic);

        ok.ShouldBeTrue();
        chatId.ShouldBe(expectedChatId);
        topic.ShouldBe(expectedTopic);
    }

    [Fact]
    public void TryDecode_EmptyAddress_ReturnsFalse()
    {
        var ok = TelegramChannelAddress.TryDecode(ChannelAddress.Empty, out var chatId, out var topic);

        ok.ShouldBeFalse();
        chatId.ShouldBe(0L);
        topic.ShouldBeNull();
    }

    [Fact]
    public void TryDecode_NonNumericPrimary_ReturnsFalse()
    {
        var ok = TelegramChannelAddress.TryDecode(ChannelAddress.From("notanumber/topic:5"), out var chatId, out var topic);

        ok.ShouldBeFalse();
        chatId.ShouldBe(0L);
        topic.ShouldBeNull();
    }

    [Fact]
    public void TryDecode_LegacyNonNumericTopic_DecodesPrimaryAndDropsTopic()
    {
        // Defensive: legacy REST bindings could carry arbitrary thread_id strings
        // that the SQLite migration appends verbatim, e.g. "12345/topic:topic:99".
        // The decoder must succeed on the primary chat ID and silently drop the
        // unparseable topic so outbound routing falls back to the root chat
        // rather than throwing.
        var ok = TelegramChannelAddress.TryDecode(ChannelAddress.From("12345/topic:topic:99"), out var chatId, out var topic);

        ok.ShouldBeTrue();
        chatId.ShouldBe(12345L);
        topic.ShouldBeNull();
    }

    [Fact]
    public void TryDecode_EmptyTopicSegment_TreatedAsNoTopic()
    {
        var ok = TelegramChannelAddress.TryDecode(ChannelAddress.From("12345/topic:"), out var chatId, out var topic);

        ok.ShouldBeTrue();
        chatId.ShouldBe(12345L);
        topic.ShouldBeNull();
    }

    [Fact]
    public void Encode_Decode_GeneralTopic_RoundTripsCleanly()
    {
        var address = TelegramChannelAddress.Encode(12345, 1);
        var ok = TelegramChannelAddress.TryDecode(address, out var chatId, out var topic);

        ok.ShouldBeTrue();
        chatId.ShouldBe(12345L);
        topic.ShouldBe(1);
    }

    [Fact]
    public void TwoDifferentTopics_InSameChat_ProduceDistinctAddresses()
    {
        var topicA = TelegramChannelAddress.Encode(12345, 7);
        var topicB = TelegramChannelAddress.Encode(12345, 42);
        var root = TelegramChannelAddress.Encode(12345, null);

        topicA.ShouldNotBe(topicB);
        topicA.ShouldNotBe(root);
        topicB.ShouldNotBe(root);
    }
}

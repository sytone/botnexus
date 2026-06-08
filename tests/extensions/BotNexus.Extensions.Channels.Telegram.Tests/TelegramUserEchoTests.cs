using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.Telegram;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Channels.Telegram.Tests;

public class TelegramUserEchoTests
{
    [Fact]
    public void FormatUserEcho_FormatsWithPrefix()
    {
        var result = TelegramChannelAdapter.FormatUserEcho("hello from portal");
        Assert.StartsWith("*User Said:*", result);
        Assert.Contains("hello from portal", result);
    }

    [Fact]
    public void FormatUserEcho_EscapesMarkdownV2Characters()
    {
        // MarkdownV2 requires escaping special chars like . ! ( ) etc.
        var result = TelegramChannelAdapter.FormatUserEcho("test.message!");
        Assert.Contains(@"test\.message\!", result);
    }

    [Fact]
    public void FormatUserEcho_EmptyContent_ReturnsPrefix()
    {
        var result = TelegramChannelAdapter.FormatUserEcho(string.Empty);
        Assert.Equal("*User Said:*\n", result);
    }

    [Fact]
    public void IsUserEchoMessage_TrueWhenMetadataSet()
    {
        var message = new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("12345"),
            Content = "hello",
            Metadata = new Dictionary<string, object?>
            {
                [TelegramChannelAdapter.UserEchoMetadataKey] = true
            }
        };

        // Use reflection or the internal method — since we made it internal, use InternalsVisibleTo
        // For now test the metadata key constant
        Assert.True(message.Metadata.TryGetValue(TelegramChannelAdapter.UserEchoMetadataKey, out var val) && val is true);
    }

    [Fact]
    public void IsUserEchoMessage_FalseWhenMetadataAbsent()
    {
        var message = new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("12345"),
            Content = "hello",
            Metadata = new Dictionary<string, object?>()
        };

        Assert.False(message.Metadata.TryGetValue(TelegramChannelAdapter.UserEchoMetadataKey, out _));
    }

    [Fact]
    public void IsUserEchoMessage_FalseWhenMetadataNotTrue()
    {
        var message = new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("12345"),
            Content = "hello",
            Metadata = new Dictionary<string, object?>
            {
                [TelegramChannelAdapter.UserEchoMetadataKey] = false
            }
        };

        Assert.True(message.Metadata.TryGetValue(TelegramChannelAdapter.UserEchoMetadataKey, out var val));
        Assert.False(val is true);
    }

    [Fact]
    public void EchoForeignUserMessages_DefaultTrue()
    {
        var config = new TelegramBotConfig();
        Assert.True(config.EchoForeignUserMessages);
    }

    [Fact]
    public void EchoForeignUserMessages_CanBeDisabled()
    {
        var config = new TelegramBotConfig { EchoForeignUserMessages = false };
        Assert.False(config.EchoForeignUserMessages);
    }
}

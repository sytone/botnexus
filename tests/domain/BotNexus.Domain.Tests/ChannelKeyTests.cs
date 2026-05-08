using System.Text.Json;
using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Tests;

public sealed class ChannelKeyTests
{
    [Fact]
    public void ChannelKey_From_WhenValueIsValid_ShouldCreateInstance()
    {
        var result = ChannelKey.From("signalr");
        result.Value.ShouldBe("signalr");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ChannelKey_From_WhenValueIsEmpty_ShouldThrowArgumentException(string? value)
    {
        Action action = () => ChannelKey.From(value!);
        action.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void ChannelKey_Constructor_WhenValueHasMixedCase_ShouldNormalizeToLowercase()
    {
        var result = new ChannelKey("  SiGnAlR ");
        result.Value.ShouldBe("signalr");
    }

    [Fact]
    public void ChannelKey_Equals_WhenValuesMatch_ShouldBeTrue()
    {
        var left = ChannelKey.From("SignalR");
        var right = ChannelKey.From("signalr");
        left.ShouldBe(right);
    }

    [Fact]
    public void ChannelKey_Equals_WhenValuesDiffer_ShouldBeFalse()
    {
        var left = ChannelKey.From("signalr");
        var right = ChannelKey.From("telegram");
        left.ShouldNotBe(right);
    }

    [Fact]
    public void ChannelKey_From_WebChatAlias_ShouldResolveToSignalr()
    {
        var alias = ChannelKey.From("web chat");
        var canonical = ChannelKey.From("signalr");
        alias.ShouldBe(canonical);
    }

    [Fact]
    public void ChannelKey_From_HyphenatedWebChatAlias_ShouldResolveToSignalr()
    {
        var alias = ChannelKey.From("Web-Chat");
        var canonical = ChannelKey.From("signalr");
        alias.ShouldBe(canonical);
    }

    [Fact]
    public void ChannelKey_ImplicitConversion_WhenConvertedToString_ShouldReturnValue()
    {
        var channelKey = ChannelKey.From("SignalR");
        string value = channelKey;
        value.ShouldBe("signalr");
    }

    [Fact]
    public void ChannelKey_ExplicitConversion_WhenConvertedFromString_ShouldCreateInstance()
    {
        var channelKey = (ChannelKey)" SignalR ";
        channelKey.Value.ShouldBe("signalr");
    }

    [Fact]
    public void ChannelKey_ToString_WhenCalled_ShouldReturnValue()
    {
        var channelKey = ChannelKey.From("SignalR");
        channelKey.ToString().ShouldBe("signalr");
    }

    [Fact]
    public void ChannelKey_JsonRoundTrip_WhenSerializedAndDeserialized_ShouldBeEqual()
    {
        var original = ChannelKey.From("SignalR");
        var roundTrip = JsonSerializer.Deserialize<ChannelKey>(JsonSerializer.Serialize(original));
        roundTrip.ShouldBe(original);
    }
}

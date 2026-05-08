using System.Text.Json;
using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Tests;

public sealed class SenderIdTests
{
    [Fact]
    public void SenderId_From_WhenValueIsValid_ShouldCreateInstance()
    {
        var result = SenderId.From(" sender-1 ");
        result.Value.ShouldBe("sender-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void SenderId_From_WhenValueIsEmpty_ShouldThrowArgumentException(string? value)
    {
        Action action = () => SenderId.From(value!);
        action.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void SenderId_Equals_WhenValuesMatch_ShouldBeTrue()
    {
        var left = SenderId.From("sender-1");
        var right = SenderId.From("sender-1");
        left.ShouldBe(right);
    }

    [Fact]
    public void SenderId_Equals_WhenValuesDiffer_ShouldBeFalse()
    {
        var left = SenderId.From("sender-1");
        var right = SenderId.From("sender-2");
        left.ShouldNotBe(right);
    }

    [Fact]
    public void SenderId_ImplicitConversion_WhenConvertedToString_ShouldReturnValue()
    {
        var id = SenderId.From("sender-1");
        string value = id;
        value.ShouldBe("sender-1");
    }

    [Fact]
    public void SenderId_ExplicitConversion_WhenConvertedFromString_ShouldCreateInstance()
    {
        var id = (SenderId)"sender-1";
        id.Value.ShouldBe("sender-1");
    }

    [Fact]
    public void SenderId_ToString_WhenCalled_ShouldReturnValue()
    {
        var id = SenderId.From("sender-1");
        id.ToString().ShouldBe("sender-1");
    }

    [Fact]
    public void SenderId_JsonRoundTrip_WhenSerializedAndDeserialized_ShouldBeEqual()
    {
        var original = SenderId.From("sender-1");
        var roundTrip = JsonSerializer.Deserialize<SenderId>(JsonSerializer.Serialize(original));
        roundTrip.ShouldBe(original);
    }
}

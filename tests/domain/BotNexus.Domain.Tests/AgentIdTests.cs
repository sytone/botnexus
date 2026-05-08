using System.Text.Json;
using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Tests;

public sealed class AgentIdTests
{
    [Fact]
    public void AgentId_From_WhenValueIsValid_ShouldCreateInstance()
    {
        var result = AgentId.From(" agent-1 ");
        result.Value.ShouldBe("agent-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void AgentId_From_WhenValueIsEmpty_ShouldThrowArgumentException(string? value)
    {
        Action action = () => AgentId.From(value!);
        action.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void AgentId_Equals_WhenValuesMatch_ShouldBeTrue()
    {
        var left = AgentId.From("agent-1");
        var right = AgentId.From("agent-1");
        left.ShouldBe(right);
    }

    [Fact]
    public void AgentId_Equals_WhenValuesDiffer_ShouldBeFalse()
    {
        var left = AgentId.From("agent-1");
        var right = AgentId.From("agent-2");
        left.ShouldNotBe(right);
    }

    [Fact]
    public void AgentId_ImplicitConversion_WhenConvertedToString_ShouldReturnValue()
    {
        var id = AgentId.From("agent-1");
        string value = id;
        value.ShouldBe("agent-1");
    }

    [Fact]
    public void AgentId_ExplicitConversion_WhenConvertedFromString_ShouldCreateInstance()
    {
        var id = (AgentId)"agent-1";
        id.Value.ShouldBe("agent-1");
    }

    [Fact]
    public void AgentId_ToString_WhenCalled_ShouldReturnValue()
    {
        var id = AgentId.From("agent-1");
        id.ToString().ShouldBe("agent-1");
    }

    [Fact]
    public void AgentId_JsonRoundTrip_WhenSerializedAndDeserialized_ShouldBeEqual()
    {
        var original = AgentId.From("agent-1");
        var roundTrip = JsonSerializer.Deserialize<AgentId>(JsonSerializer.Serialize(original));
        roundTrip.ShouldBe(original);
    }
}

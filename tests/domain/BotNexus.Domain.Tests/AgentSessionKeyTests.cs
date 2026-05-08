using System.Text.Json;
using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Tests;

public sealed class AgentSessionKeyTests
{
    [Fact]
    public void AgentSessionKey_From_WhenValuesAreValid_ShouldCreateInstance()
    {
        var key = AgentSessionKey.From(AgentId.From("agent-1"), SessionId.From("session-1"));
        key.AgentId.Value.ShouldBe("agent-1");
    }

    [Fact]
    public void AgentSessionKey_Parse_WhenValueIsEmpty_ShouldThrowArgumentException()
    {
        Action action = () => AgentSessionKey.Parse(" ");
        action.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void AgentSessionKey_Parse_WhenValueIsInvalidFormat_ShouldThrowArgumentException()
    {
        Action action = () => AgentSessionKey.Parse("invalid");
        action.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void AgentSessionKey_Parse_WhenValueIsValid_ShouldCreateInstance()
    {
        var key = AgentSessionKey.Parse("agent-1::session-1");
        key.SessionId.Value.ShouldBe("session-1");
    }

    [Fact]
    public void AgentSessionKey_Equals_WhenValuesMatch_ShouldBeTrue()
    {
        var left = AgentSessionKey.From(AgentId.From("agent-1"), SessionId.From("session-1"));
        var right = AgentSessionKey.From(AgentId.From("agent-1"), SessionId.From("session-1"));
        left.ShouldBe(right);
    }

    [Fact]
    public void AgentSessionKey_Equals_WhenValuesDiffer_ShouldBeFalse()
    {
        var left = AgentSessionKey.From(AgentId.From("agent-1"), SessionId.From("session-1"));
        var right = AgentSessionKey.From(AgentId.From("agent-2"), SessionId.From("session-1"));
        left.ShouldNotBe(right);
    }

    [Fact]
    public void AgentSessionKey_ToString_WhenCalled_ShouldReturnComposedValue()
    {
        var key = AgentSessionKey.From(AgentId.From("agent-1"), SessionId.From("session-1"));
        key.ToString().ShouldBe("agent-1::session-1");
    }

    [Fact]
    public void AgentSessionKey_JsonRoundTrip_WhenSerializedAndDeserialized_ShouldBeEqual()
    {
        var original = AgentSessionKey.From(AgentId.From("agent-1"), SessionId.From("session-1"));
        var roundTrip = JsonSerializer.Deserialize<AgentSessionKey>(JsonSerializer.Serialize(original));
        roundTrip.ShouldBe(original);
    }
}

using System.Text.Json;
using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Tests;

public sealed class SessionIdTests
{
    [Fact]
    public void SessionId_From_WhenValueIsValid_ShouldCreateInstance()
    {
        var result = SessionId.From(" session-1 ");
        result.Value.ShouldBe("session-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void SessionId_From_WhenValueIsEmpty_ShouldThrowArgumentException(string? value)
    {
        Action action = () => SessionId.From(value!);
        action.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void SessionId_Create_WhenCalled_ShouldGenerateNonEmptyValue()
    {
        var sessionId = SessionId.Create();
        sessionId.Value.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SessionId_ForSubAgent_WhenValuesAreValid_ShouldUseExpectedFormat()
    {
        var sessionId = SessionId.ForSubAgent("parent-id", " child ");
        sessionId.Value.ShouldBe("parent-id::subagent::child");
    }

    [Fact]
    public void SessionId_ForSubAgent_WhenUniqueIdIsEmpty_ShouldThrowArgumentException()
    {
        Action action = () => SessionId.ForSubAgent("parent-id", " ");
        action.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void SessionId_ForCrossAgent_WhenValuesAreValid_ShouldUseExpectedFormat()
    {
        var sessionId = SessionId.ForCrossAgent("source-id", "target-id");
        sessionId.Value.ShouldBe("xagent::source-id::target-id");
    }

    [Fact]
    public void SessionId_ForAgentConversation_WhenValuesAreValid_ShouldUseExpectedFormat()
    {
        var sessionId = SessionId.ForAgentConversation("agent-a", "agent-b", "abc123");
        sessionId.Value.ShouldBe("agent-a::agent-agent::agent-b::abc123");
    }

    [Fact]
    public void SessionId_IsAgentConversation_WhenPatternMatches_ShouldBeTrue()
    {
        var sessionId = SessionId.ForAgentConversation("agent-a", "agent-b", "abc123");
        sessionId.IsAgentConversation.ShouldBeTrue();
    }

    [Fact]
    public void SessionId_Equals_WhenValuesMatch_ShouldBeTrue()
    {
        var left = SessionId.From("session-1");
        var right = SessionId.From("session-1");
        left.ShouldBe(right);
    }

    [Fact]
    public void SessionId_Equals_WhenValuesDiffer_ShouldBeFalse()
    {
        var left = SessionId.From("session-1");
        var right = SessionId.From("session-2");
        left.ShouldNotBe(right);
    }

    [Fact]
    public void SessionId_ImplicitConversion_WhenConvertedToString_ShouldReturnValue()
    {
        var id = SessionId.From("session-1");
        string value = id;
        value.ShouldBe("session-1");
    }

    [Fact]
    public void SessionId_ExplicitConversion_WhenConvertedFromString_ShouldCreateInstance()
    {
        var id = (SessionId)"session-1";
        id.Value.ShouldBe("session-1");
    }

    [Fact]
    public void SessionId_ToString_WhenCalled_ShouldReturnValue()
    {
        var id = SessionId.From("session-1");
        id.ToString().ShouldBe("session-1");
    }

    [Fact]
    public void SessionId_JsonRoundTrip_WhenSerializedAndDeserialized_ShouldBeEqual()
    {
        var original = SessionId.From("session-1");
        var roundTrip = JsonSerializer.Deserialize<SessionId>(JsonSerializer.Serialize(original));
        roundTrip.ShouldBe(original);
    }
}

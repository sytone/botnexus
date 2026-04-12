using System.Text.Json;
using BotNexus.Domain.Primitives;
using FluentAssertions;

namespace BotNexus.Domain.Tests;

public sealed class SessionIdTests
{
    [Fact]
    public void SessionId_From_WhenValueIsValid_ShouldCreateInstance()
    {
        var result = SessionId.From(" session-1 ");
        result.Value.Should().Be("session-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void SessionId_From_WhenValueIsEmpty_ShouldThrowArgumentException(string? value)
    {
        var action = () => SessionId.From(value!);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SessionId_Create_WhenCalled_ShouldGenerateNonEmptyValue()
    {
        var sessionId = SessionId.Create();
        sessionId.Value.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SessionId_ForSubAgent_WhenValuesAreValid_ShouldUseExpectedFormat()
    {
        var sessionId = SessionId.ForSubAgent("parent-id", " child ");
        sessionId.Value.Should().Be("parent-id::subagent::child");
    }

    [Fact]
    public void SessionId_ForSubAgent_WhenUniqueIdIsEmpty_ShouldThrowArgumentException()
    {
        var action = () => SessionId.ForSubAgent("parent-id", " ");
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SessionId_ForCrossAgent_WhenValuesAreValid_ShouldUseExpectedFormat()
    {
        var sessionId = SessionId.ForCrossAgent("source-id", "target-id");
        sessionId.Value.Should().Be("xagent::source-id::target-id");
    }

    [Fact]
    public void SessionId_ForAgentConversation_WhenValuesAreValid_ShouldUseExpectedFormat()
    {
        var sessionId = SessionId.ForAgentConversation("agent-a", "agent-b", "abc123");
        sessionId.Value.Should().Be("agent-a::agent-agent::agent-b::abc123");
    }

    [Fact]
    public void SessionId_IsAgentConversation_WhenPatternMatches_ShouldBeTrue()
    {
        var sessionId = SessionId.ForAgentConversation("agent-a", "agent-b", "abc123");
        sessionId.IsAgentConversation.Should().BeTrue();
    }

    [Fact]
    public void SessionId_Equals_WhenValuesMatch_ShouldBeTrue()
    {
        var left = SessionId.From("session-1");
        var right = SessionId.From("session-1");
        left.Should().Be(right);
    }

    [Fact]
    public void SessionId_Equals_WhenValuesDiffer_ShouldBeFalse()
    {
        var left = SessionId.From("session-1");
        var right = SessionId.From("session-2");
        left.Should().NotBe(right);
    }

    [Fact]
    public void SessionId_ImplicitConversion_WhenConvertedToString_ShouldReturnValue()
    {
        var id = SessionId.From("session-1");
        string value = id;
        value.Should().Be("session-1");
    }

    [Fact]
    public void SessionId_ExplicitConversion_WhenConvertedFromString_ShouldCreateInstance()
    {
        var id = (SessionId)"session-1";
        id.Value.Should().Be("session-1");
    }

    [Fact]
    public void SessionId_ToString_WhenCalled_ShouldReturnValue()
    {
        var id = SessionId.From("session-1");
        id.ToString().Should().Be("session-1");
    }

    [Fact]
    public void SessionId_JsonRoundTrip_WhenSerializedAndDeserialized_ShouldBeEqual()
    {
        var original = SessionId.From("session-1");
        var roundTrip = JsonSerializer.Deserialize<SessionId>(JsonSerializer.Serialize(original));
        roundTrip.Should().Be(original);
    }
}

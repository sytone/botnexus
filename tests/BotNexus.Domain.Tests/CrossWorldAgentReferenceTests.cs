using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Tests;

public sealed class CrossWorldAgentReferenceTests
{
    [Fact]
    public void TryParse_WithQualifiedAgentId_ReturnsWorldAndAgent()
    {
        var parsed = CrossWorldAgentReference.TryParse(AgentId.From("world-b:agent-c"), out var reference);

        parsed.ShouldBeTrue();
        reference.ShouldNotBeNull();
        reference!.WorldId.ShouldBe("world-b");
        reference.AgentId.ShouldBe(AgentId.From("agent-c"));
    }

    [Fact]
    public void TryParse_WithLocalAgentId_ReturnsFalse()
    {
        var parsed = CrossWorldAgentReference.TryParse(AgentId.From("agent-c"), out var reference);

        parsed.ShouldBeFalse();
        reference.ShouldBeNull();
    }
}

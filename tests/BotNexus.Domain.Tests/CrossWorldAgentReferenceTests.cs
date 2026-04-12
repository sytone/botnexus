using BotNexus.Domain.Conversations;
using BotNexus.Domain.Primitives;
using FluentAssertions;

namespace BotNexus.Domain.Tests;

public sealed class CrossWorldAgentReferenceTests
{
    [Fact]
    public void TryParse_WithQualifiedAgentId_ReturnsWorldAndAgent()
    {
        var parsed = CrossWorldAgentReference.TryParse(AgentId.From("world-b:leela"), out var reference);

        parsed.Should().BeTrue();
        reference.Should().NotBeNull();
        reference!.WorldId.Should().Be("world-b");
        reference.AgentId.Should().Be(AgentId.From("leela"));
    }

    [Fact]
    public void TryParse_WithLocalAgentId_ReturnsFalse()
    {
        var parsed = CrossWorldAgentReference.TryParse(AgentId.From("leela"), out var reference);

        parsed.Should().BeFalse();
        reference.Should().BeNull();
    }
}

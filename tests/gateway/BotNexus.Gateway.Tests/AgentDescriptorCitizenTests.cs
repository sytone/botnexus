using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Tests;

public sealed class AgentDescriptorCitizenTests
{
    [Fact]
    public void ImplementsICitizen()
    {
        var descriptor = CreateDescriptor();

        descriptor.ShouldBeAssignableTo<ICitizen>();
    }

    [Fact]
    public void ICitizenId_WrapsAgentIdAsAgentCitizen()
    {
        ICitizen descriptor = CreateDescriptor();

        descriptor.Id.Kind.ShouldBe(CitizenKind.Agent);
        descriptor.Id.AsAgent.ShouldNotBeNull();
        descriptor.Id.AsAgent!.Value.Value.ShouldBe("agent-a");
        descriptor.Id.AsUser.ShouldBeNull();
    }

    [Fact]
    public void ICitizenDisplayName_MatchesDescriptorDisplayName()
    {
        ICitizen descriptor = CreateDescriptor();

        descriptor.DisplayName.ShouldBe("Agent A");
    }

    [Fact]
    public void TwoDescriptorsWithSameAgentId_ExposeEqualCitizenIds()
    {
        ICitizen a = CreateDescriptor();
        ICitizen b = CreateDescriptor();

        a.Id.ShouldBe(b.Id);
    }

    private static AgentDescriptor CreateDescriptor()
        => new()
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "model",
            ApiProvider = "provider",
            SystemPrompt = "Prompt",
            MaxConcurrentSessions = 1,
        };
}

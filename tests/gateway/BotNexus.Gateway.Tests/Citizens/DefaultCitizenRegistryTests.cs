using BotNexus.Domain;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Citizens;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Citizens;

public sealed class DefaultCitizenRegistryTests
{
    [Fact]
    public void Resolve_UserCitizenId_ReturnsUserFromUserRegistry()
    {
        var users = CreateUserRegistry();
        var agents = CreateAgentRegistry();
        users.Register(CreateUser("alice"));

        var citizen = new DefaultCitizenRegistry(users, agents).Resolve(CitizenId.Of(UserId.From("alice")));

        citizen.ShouldNotBeNull();
        citizen.ShouldBeOfType<User>();
        citizen!.Id.AsUser!.Value.Value.ShouldBe("alice");
    }

    [Fact]
    public void Resolve_AgentCitizenId_ReturnsAgentFromAgentRegistry()
    {
        var users = CreateUserRegistry();
        var agents = CreateAgentRegistry();
        agents.Register(CreateAgent("agent-a"));

        var citizen = new DefaultCitizenRegistry(users, agents).Resolve(CitizenId.Of(AgentId.From("agent-a")));

        citizen.ShouldNotBeNull();
        citizen.ShouldBeOfType<AgentDescriptor>();
        citizen!.Id.AsAgent!.Value.Value.ShouldBe("agent-a");
    }

    [Fact]
    public void Resolve_UnknownCitizenId_ReturnsNull()
    {
        var registry = new DefaultCitizenRegistry(CreateUserRegistry(), CreateAgentRegistry());

        registry.Resolve(CitizenId.Of(UserId.From("ghost"))).ShouldBeNull();
        registry.Resolve(CitizenId.Of(AgentId.From("ghost"))).ShouldBeNull();
    }

    [Fact]
    public void Resolve_DefaultCitizenId_ReturnsNull()
    {
        var registry = new DefaultCitizenRegistry(CreateUserRegistry(), CreateAgentRegistry());

        registry.Resolve(default).ShouldBeNull();
    }

    [Fact]
    public void GetAll_ReturnsBothUsersAndAgents_NeverDropsASpecies()
    {
        var users = CreateUserRegistry();
        var agents = CreateAgentRegistry();
        users.Register(CreateUser("alice"));
        users.Register(CreateUser("bob"));
        agents.Register(CreateAgent("agent-a"));

        var combined = new DefaultCitizenRegistry(users, agents).GetAll();

        combined.Count.ShouldBe(3);
        combined.OfType<User>().Count().ShouldBe(2);
        combined.OfType<AgentDescriptor>().Count().ShouldBe(1);
    }

    [Fact]
    public void Contains_TrueWhenSpeciesRegistryContains_FalseOtherwise()
    {
        var users = CreateUserRegistry();
        var agents = CreateAgentRegistry();
        users.Register(CreateUser("alice"));
        agents.Register(CreateAgent("agent-a"));
        var registry = new DefaultCitizenRegistry(users, agents);

        registry.Contains(CitizenId.Of(UserId.From("alice"))).ShouldBeTrue();
        registry.Contains(CitizenId.Of(AgentId.From("agent-a"))).ShouldBeTrue();
        registry.Contains(CitizenId.Of(UserId.From("missing"))).ShouldBeFalse();
        registry.Contains(CitizenId.Of(AgentId.From("missing"))).ShouldBeFalse();
        registry.Contains(default).ShouldBeFalse();
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        Action nullUsers = () => new DefaultCitizenRegistry(null!, CreateAgentRegistry());
        Action nullAgents = () => new DefaultCitizenRegistry(CreateUserRegistry(), null!);

        nullUsers.ShouldThrow<ArgumentNullException>();
        nullAgents.ShouldThrow<ArgumentNullException>();
    }

    private static DefaultUserRegistry CreateUserRegistry()
        => new(NullLogger<DefaultUserRegistry>.Instance);

    private static DefaultAgentRegistry CreateAgentRegistry()
        => new(NullLogger<DefaultAgentRegistry>.Instance);

    private static User CreateUser(string id)
        => new()
        {
            Id = UserId.From(id),
            DisplayName = id,
            World = new WorldIdentity { Id = "world-a", Name = "World A" },
        };

    private static AgentDescriptor CreateAgent(string id)
        => new()
        {
            AgentId = AgentId.From(id),
            DisplayName = id,
            ModelId = "model",
            ApiProvider = "provider",
            SystemPrompt = "p",
            MaxConcurrentSessions = 1,
        };
}

using BotNexus.Domain;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;

namespace BotNexus.Domain.Tests;

public sealed class UserTests
{
    [Fact]
    public void Required_PropertiesAreInitialised_FromObjectInitializer()
    {
        var user = CreateUser("alice", "Alice");

        user.Id.Value.ShouldBe("alice");
        user.DisplayName.ShouldBe("Alice");
        user.World.Id.ShouldBe("world-a");
        user.ChannelIdentities.ShouldBeEmpty();
    }

    [Fact]
    public void ICitizenId_ReturnsCitizenIdOfUserId()
    {
        ICitizen citizen = CreateUser("alice", "Alice");

        citizen.Id.Kind.ShouldBe(CitizenKind.User);
        citizen.Id.AsUser.ShouldNotBeNull();
        citizen.Id.AsUser!.Value.Value.ShouldBe("alice");
        citizen.Id.AsAgent.ShouldBeNull();
    }

    [Fact]
    public void ICitizenDisplayName_MatchesUserDisplayName()
    {
        ICitizen citizen = CreateUser("alice", "Alice");

        citizen.DisplayName.ShouldBe("Alice");
    }

    [Fact]
    public void TwoUsersWithSameId_ExposeEqualCitizenIds()
    {
        ICitizen a = CreateUser("alice", "Alice");
        ICitizen b = CreateUser("alice", "Alicia");

        a.Id.ShouldBe(b.Id);
    }

    [Fact]
    public void User_AndAgentDescriptor_WithSameLocalIdAreNotEqualCitizens()
    {
        var user = CreateUser("alpha", "User Alpha");
        var agent = new BotNexus.Gateway.Abstractions.Models.AgentDescriptor
        {
            AgentId = AgentId.From("alpha"),
            DisplayName = "Agent Alpha",
            ModelId = "model",
            ApiProvider = "provider",
            SystemPrompt = "p",
            MaxConcurrentSessions = 1,
        };

        ((ICitizen)user).Id.ShouldNotBe(((ICitizen)agent).Id);
    }

    [Fact]
    public void ChannelIdentities_AreInitiallyEmpty_WhenNotProvided()
    {
        var user = CreateUser("bob", "Bob");

        user.ChannelIdentities.ShouldBeEmpty();
    }

    [Fact]
    public void ChannelIdentities_PreserveOrder_AsSupplied()
    {
        var user = new User
        {
            Id = UserId.From("bob"),
            DisplayName = "Bob",
            World = CreateWorld(),
            ChannelIdentities =
            [
                new ChannelIdentity(ChannelKey.From("telegram"), ChannelAddress.From("555-001")),
                new ChannelIdentity(ChannelKey.From("signalr"), ChannelAddress.From("conn-99")),
            ],
        };

        user.ChannelIdentities.Count.ShouldBe(2);
        user.ChannelIdentities[0].Channel.Value.ShouldBe("telegram");
        user.ChannelIdentities[1].Channel.Value.ShouldBe("signalr");
    }

    private static User CreateUser(string id, string displayName)
        => new()
        {
            Id = UserId.From(id),
            DisplayName = displayName,
            World = CreateWorld(),
        };

    private static WorldIdentity CreateWorld()
        => new() { Id = "world-a", Name = "World A" };
}

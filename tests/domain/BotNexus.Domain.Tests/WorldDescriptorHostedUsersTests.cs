using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;

namespace BotNexus.Domain.Tests;

public sealed class WorldDescriptorHostedUsersTests
{
    [Fact]
    public void HostedUsers_DefaultsToEmpty_WhenOmitted()
    {
        var world = new WorldDescriptor { Identity = CreateIdentity() };

        world.HostedUsers.ShouldBeEmpty();
    }

    [Fact]
    public void HostedAgents_StillBacksFill_AlongsideNewHostedUsers()
    {
        var world = new WorldDescriptor
        {
            Identity = CreateIdentity(),
            HostedAgents = [AgentId.From("agent-a")],
        };

        world.HostedAgents.Count.ShouldBe(1);
        world.HostedUsers.ShouldBeEmpty();
    }

    [Fact]
    public void HostedUsers_PersistAssignedValues_InDeclarationOrder()
    {
        var world = new WorldDescriptor
        {
            Identity = CreateIdentity(),
            HostedUsers = [UserId.From("alice"), UserId.From("bob")],
        };

        world.HostedUsers.Select(u => u.Value).ShouldBe(["alice", "bob"]);
    }

    private static WorldIdentity CreateIdentity()
        => new() { Id = "world-a", Name = "World A" };
}

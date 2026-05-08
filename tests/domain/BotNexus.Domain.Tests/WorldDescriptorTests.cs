using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;

namespace BotNexus.Domain.Tests;

public sealed class WorldDescriptorTests
{
    [Fact]
    public void WorldDescriptor_JsonRoundTrip_WhenSerializedAndDeserialized_PreservesValues()
    {
        var original = new WorldDescriptor
        {
            Identity = new BotNexus.Domain.WorldIdentity
            {
                Id = "local-dev",
                Name = "Local Development",
                Description = "Local development gateway",
                Emoji = "🏠"
            },
            HostedAgents = [AgentId.From("assistant")],
            Locations =
            [
                new Location
                {
                    Name = "agents-directory",
                    Type = LocationType.FileSystem,
                    Path = Path.Combine(Path.GetTempPath(), "agents"),
                    Description = "Gateway agent storage",
                    Properties = new Dictionary<string, string> { ["scope"] = "gateway" }
                }
            ],
            AvailableStrategies = [ExecutionStrategy.InProcess],
            CrossWorldPermissions =
            [
                new CrossWorldPermission
                {
                    TargetWorldId = "prod",
                    AllowedAgents = [AgentId.From("assistant")],
                    AllowInbound = true,
                    AllowOutbound = false
                }
            ]
        };

        var json = JsonSerializer.Serialize(original);
        var roundTrip = JsonSerializer.Deserialize<WorldDescriptor>(json);

        roundTrip.ShouldNotBeNull();
        roundTrip!.Identity.Id.ShouldBe("local-dev");
        roundTrip.Identity.Name.ShouldBe("Local Development");
        roundTrip.HostedAgents.ShouldHaveSingleItem().Value.ShouldBe("assistant");
        var singleLocation = roundTrip.Locations.ShouldHaveSingleItem();
        singleLocation.Type.ShouldBe(LocationType.FileSystem);
        singleLocation.Path.ShouldBe(Path.Combine(Path.GetTempPath(), "agents"));
        singleLocation.Description.ShouldBe("Gateway agent storage");
        roundTrip.AvailableStrategies.ShouldHaveSingleItem().ShouldBe(ExecutionStrategy.InProcess);
        var singlePermission = roundTrip.CrossWorldPermissions.ShouldHaveSingleItem();
        singlePermission.TargetWorldId.ShouldBe("prod");
        singlePermission.AllowOutbound.ShouldBeFalse();
    }

    [Fact]
    public void CrossWorldPermission_Defaults_AllowBidirectionalAccessWhenNotConfigured()
    {
        var permission = new CrossWorldPermission
        {
            TargetWorldId = "prod"
        };

        permission.TargetWorldId.ShouldBe("prod");
        permission.AllowedAgents.ShouldBeNull();
        permission.AllowInbound.ShouldBeTrue();
        permission.AllowOutbound.ShouldBeTrue();
    }
}

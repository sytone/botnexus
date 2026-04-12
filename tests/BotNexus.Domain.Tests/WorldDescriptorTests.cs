using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using FluentAssertions;

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
                    Path = "C:\\agents",
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

        roundTrip.Should().NotBeNull();
        roundTrip!.Identity.Id.Should().Be("local-dev");
        roundTrip.Identity.Name.Should().Be("Local Development");
        roundTrip.HostedAgents.Should().ContainSingle(agent => agent.Value == "assistant");
        roundTrip.Locations.Should().ContainSingle(location => location.Type == LocationType.FileSystem && location.Path == "C:\\agents");
        roundTrip.AvailableStrategies.Should().ContainSingle(strategy => strategy == ExecutionStrategy.InProcess);
        roundTrip.CrossWorldPermissions.Should().ContainSingle(permission => permission.TargetWorldId == "prod" && !permission.AllowOutbound);
    }
}

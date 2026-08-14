using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using Moq;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class WorldDescriptorBuilderTests
{
    /// <summary>
    /// A fixture-owned absolute root that every configured path in this test is rooted at.
    /// </summary>
    /// <remarks>
    /// This test used to configure <c>~/repo</c> and assert against
    /// <c>Path.Combine(GetExpectedUserProfile(), "repo")</c>, re-resolving the user profile a second
    /// time at assertion time. That made the expected value a statement about the host's ambient
    /// environment at the instant the assertion ran rather than about the builder: any test running
    /// concurrently that mutated <c>HOME</c>/<c>USERPROFILE</c> - or any disagreement between the two
    /// resolutions - reddened it with no production defect. It duly passed and failed on identical
    /// commit content in two remote runs ten minutes apart (issue #3149).
    /// Rooting the fixture at a controlled absolute path removes the ambient dependency by
    /// construction: there is no environment state left for a concurrent test to perturb. The
    /// <c>~</c>-expansion semantics this test incidentally exercised are owned by
    /// <c>HomePathExpanderTests</c> and fenced by <c>HomePathExpansionArchitectureTests</c>, which is
    /// where they belong; this test's contract is aggregation.
    /// </remarks>
    private static readonly string FixtureRoot =
        Path.Combine(Path.GetTempPath(), "botnexus-world-descriptor-tests");

    [Fact]
    public void Build_AggregatesIdentityAgentsLocationsStrategiesAndPermissions()
    {
        var mcpExtension = JsonDocument.Parse("""
            {
              "servers": {
                "github": {
                  "command": "npx",
                  "args": ["-y", "@modelcontextprotocol/server-github"]
                }
              }
            }
            """).RootElement.Clone();

        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                World = new BotNexus.Domain.WorldIdentity
                {
                    Id = "local-dev",
                    Name = "Local Development",
                    Description = "Local gateway",
                    Emoji = "🏠"
                },
                ListenUrl = "http://localhost:5005",
                AgentsDirectory = Path.Combine(Path.GetTempPath(), "botnexus", "agents"),
                SessionsDirectory = Path.Combine(Path.GetTempPath(), "botnexus", "sessions"),
                Locations = new Dictionary<string, LocationConfig>
                {
                    ["provider:copilot"] = new()
                    {
                        Type = "filesystem",
                        Path = Path.Combine(FixtureRoot, "declared-provider"),
                        Description = "declared takes precedence",
                        Properties = new Dictionary<string, string> { ["source"] = "declared" }
                    },
                    ["repo-root"] = new()
                    {
                        Type = "filesystem",
                        Path = Path.Combine(FixtureRoot, "repo"),
                        Description = "repository root"
                    }
                },
                CrossWorldPermissions =
                [
                    new CrossWorldPermissionConfig
                    {
                        TargetWorldId = "prod",
                        AllowedAgents = ["assistant"],
                        AllowInbound = true,
                        AllowOutbound = false
                    }
                ]
            },
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new()
                {
                    Enabled = true,
                    IsolationStrategy = "sandbox",
                    Extensions = new Dictionary<string, JsonElement>
                    {
                        ["botnexus-mcp"] = mcpExtension
                    }
                },
                ["disabled-agent"] = new() { Enabled = false }
            },
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["copilot"] = new() { Enabled = true, BaseUrl = "https://api.githubcopilot.com" }
            }
        };

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(x => x.GetAll()).Returns(
        [
            new AgentDescriptor
            {
                AgentId = AgentId.From("assistant"),
                DisplayName = "Assistant",
                ModelId = "gpt-4.1",
                ApiProvider = "copilot",
                IsolationStrategy = "container"
            },
            new AgentDescriptor
            {
                AgentId = AgentId.From("runtime-agent"),
                DisplayName = "Runtime Agent",
                ModelId = "gpt-4.1",
                ApiProvider = "copilot",
                IsolationStrategy = "remote"
            }
        ]);

        var world = WorldDescriptorBuilder.Build(
            config,
            registry.Object,
            [new StubIsolationStrategy("in-process"), new StubIsolationStrategy("sandbox")]);

        world.Identity.Id.ShouldBe("local-dev");
        world.HostedAgents.Select(agent => agent.Value).ShouldContain("assistant");
        world.HostedAgents.Select(agent => agent.Value).ShouldContain("runtime-agent");
        world.HostedAgents.Select(agent => agent.Value).ShouldNotContain("disabled-agent");

        world.AvailableStrategies.Select(strategy => strategy.Value)
            .ShouldContain("in-process");
        world.AvailableStrategies.Select(strategy => strategy.Value).ShouldContain("sandbox");
        world.AvailableStrategies.Select(strategy => strategy.Value).ShouldContain("container");
        world.AvailableStrategies.Select(strategy => strategy.Value).ShouldContain("remote");

        world.Locations.ShouldContain(location => location.Name == "agents-directory" && location.Type == LocationType.FileSystem);
        world.Locations.ShouldContain(location => location.Name == "sessions-directory" && location.Type == LocationType.FileSystem);
        world.Locations.ShouldContain(location =>
            location.Name == "provider:copilot"
            && location.Type == LocationType.FileSystem
            && location.Description == "declared takes precedence"
            && location.Properties["source"] == "declared");
        world.Locations.ShouldContain(location => location.Name == "mcp:assistant:github" && location.Type == LocationType.McpServer);
        world.Locations.ShouldContain(location => location.Name == "agent:assistant:workspace" && location.Type == LocationType.FileSystem);
        world.Locations.ShouldContain(location =>
            location.Name == "repo-root"
            && location.Type == LocationType.FileSystem
            && location.Path == Path.GetFullPath(Path.Combine(FixtureRoot, "repo")));

        var permission = world.CrossWorldPermissions.ShouldHaveSingleItem();
        permission.TargetWorldId.ShouldBe("prod");
        permission.AllowInbound.ShouldBeTrue();
        permission.AllowOutbound.ShouldBeFalse();
        permission.AllowedAgents.ShouldHaveSingleItem();
        permission.AllowedAgents![0].Value.ShouldBe("assistant");
    }

    private sealed class StubIsolationStrategy(string name) : IIsolationStrategy
    {
        public string Name { get; } = name;

        public Task<IAgentHandle> CreateAsync(
            AgentDescriptor descriptor,
            AgentExecutionContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

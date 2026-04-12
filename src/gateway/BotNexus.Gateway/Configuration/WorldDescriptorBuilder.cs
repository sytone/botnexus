using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;

namespace BotNexus.Gateway.Configuration;

public static class WorldDescriptorBuilder
{
    public static WorldDescriptor Build(
        PlatformConfig? config,
        IAgentRegistry? agentRegistry,
        IEnumerable<IIsolationStrategy>? isolationStrategies)
    {
        var platformConfig = config ?? new PlatformConfig();
        var identity = WorldIdentityResolver.Resolve(platformConfig);
        var hostedAgents = ResolveHostedAgents(platformConfig, agentRegistry);

        return new WorldDescriptor
        {
            Identity = identity,
            HostedAgents = hostedAgents,
            Locations = ResolveLocations(platformConfig, hostedAgents),
            AvailableStrategies = ResolveExecutionStrategies(platformConfig, agentRegistry, isolationStrategies),
            CrossWorldPermissions = ResolveCrossWorldPermissions(platformConfig)
        };
    }

    private static IReadOnlyList<AgentId> ResolveHostedAgents(PlatformConfig config, IAgentRegistry? agentRegistry)
    {
        var agentIds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        if (config.Agents is not null)
        {
            foreach (var (agentId, agentConfig) in config.Agents)
            {
                if (agentConfig.Enabled)
                    agentIds.Add(agentId);
            }
        }

        if (agentRegistry is not null)
        {
            foreach (var descriptor in agentRegistry.GetAll())
                agentIds.Add(descriptor.AgentId.Value);
        }

        return agentIds.Select(AgentId.From).ToArray();
    }

    private static IReadOnlyList<ExecutionStrategy> ResolveExecutionStrategies(
        PlatformConfig config,
        IAgentRegistry? agentRegistry,
        IEnumerable<IIsolationStrategy>? isolationStrategies)
    {
        var strategyNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        if (config.Agents is not null)
        {
            foreach (var agent in config.Agents.Values.Where(agent => agent.Enabled))
                strategyNames.Add(string.IsNullOrWhiteSpace(agent.IsolationStrategy) ? "in-process" : agent.IsolationStrategy);
        }

        if (agentRegistry is not null)
        {
            foreach (var descriptor in agentRegistry.GetAll())
            {
                if (!string.IsNullOrWhiteSpace(descriptor.IsolationStrategy))
                    strategyNames.Add(descriptor.IsolationStrategy);
            }
        }

        if (isolationStrategies is not null)
        {
            foreach (var strategy in isolationStrategies)
            {
                if (!string.IsNullOrWhiteSpace(strategy.Name))
                    strategyNames.Add(strategy.Name);
            }
        }

        return strategyNames.Select(ExecutionStrategy.FromString).ToArray();
    }

    private static IReadOnlyList<Location> ResolveLocations(PlatformConfig config, IReadOnlyList<AgentId> hostedAgents)
    {
        Dictionary<string, Location> locations = new(StringComparer.OrdinalIgnoreCase);

        static string? NormalizePath(string? path) => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

        void UpsertLocation(Location location) => locations[location.Name] = location;

        var homePath = BotNexusHome.ResolveHomePath();
        var agentsDirectory = NormalizePath(config.Gateway?.AgentsDirectory) ?? Path.Combine(homePath, "agents");
        var sessionsDirectory = NormalizePath(config.Gateway?.SessionsDirectory) ?? Path.Combine(homePath, "sessions");
        var extensionsDirectory = NormalizePath(config.Gateway?.Extensions?.Path) ?? Path.Combine(homePath, "extensions");

        UpsertLocation(new Location
        {
            Name = "agents-directory",
            Type = LocationType.FileSystem,
            Path = agentsDirectory,
            Properties = new Dictionary<string, string> { ["scope"] = "gateway" }
        });

        UpsertLocation(new Location
        {
            Name = "sessions-directory",
            Type = LocationType.FileSystem,
            Path = sessionsDirectory,
            Properties = new Dictionary<string, string> { ["scope"] = "gateway" }
        });

        UpsertLocation(new Location
        {
            Name = "extensions-directory",
            Type = LocationType.FileSystem,
            Path = extensionsDirectory,
            Properties = new Dictionary<string, string> { ["scope"] = "gateway" }
        });

        if (!string.IsNullOrWhiteSpace(config.Gateway?.ListenUrl))
        {
            UpsertLocation(new Location
            {
                Name = "gateway-api",
                Type = LocationType.Api,
                Path = config.Gateway.ListenUrl,
                Properties = new Dictionary<string, string> { ["source"] = "gateway.listenUrl" }
            });
        }

        foreach (var hostedAgent in hostedAgents)
        {
            UpsertLocation(new Location
            {
                Name = $"agent:{hostedAgent.Value}:workspace",
                Type = LocationType.FileSystem,
                Path = Path.Combine(agentsDirectory, hostedAgent.Value, "workspace"),
                Properties = new Dictionary<string, string> { ["agentId"] = hostedAgent.Value }
            });
        }

        if (config.Providers is not null)
        {
            foreach (var (providerName, providerConfig) in config.Providers)
            {
                if (!providerConfig.Enabled || string.IsNullOrWhiteSpace(providerConfig.BaseUrl))
                    continue;

                UpsertLocation(new Location
                {
                    Name = $"provider:{providerName}",
                    Type = LocationType.Api,
                    Path = providerConfig.BaseUrl,
                    Properties = new Dictionary<string, string> { ["provider"] = providerName }
                });
            }
        }

        if (config.Agents is not null)
        {
            foreach (var (agentId, agentConfig) in config.Agents)
            {
                if (!agentConfig.Enabled || agentConfig.Extensions is null)
                    continue;

                foreach (var extensionId in new[] { "botnexus-mcp", "botnexus-mcp-invoke" })
                {
                    if (!agentConfig.Extensions.TryGetValue(extensionId, out var extensionConfig))
                        continue;

                    if (!extensionConfig.TryGetProperty("servers", out var serversElement) || serversElement.ValueKind != JsonValueKind.Object)
                        continue;

                    foreach (var server in serversElement.EnumerateObject())
                    {
                        var serverConfig = server.Value;
                        string? endpoint = null;

                        if (serverConfig.ValueKind == JsonValueKind.Object)
                        {
                            endpoint = ReadString(serverConfig, "url");
                            if (string.IsNullOrWhiteSpace(endpoint))
                            {
                                var command = ReadString(serverConfig, "command");
                                var args = serverConfig.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array
                                    ? string.Join(' ', argsElement.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)))
                                    : null;

                                if (!string.IsNullOrWhiteSpace(command))
                                    endpoint = string.IsNullOrWhiteSpace(args) ? command : $"{command} {args}";
                            }
                        }

                        var locationName = $"mcp:{agentId}:{server.Name}";
                        UpsertLocation(new Location
                        {
                            Name = locationName,
                            Type = LocationType.McpServer,
                            Path = endpoint,
                            Properties = new Dictionary<string, string>
                            {
                                ["agentId"] = agentId,
                                ["serverId"] = server.Name,
                                ["extensionId"] = extensionId
                            }
                        });
                    }
                }
            }
        }

        return locations.Values
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<CrossWorldPermission> ResolveCrossWorldPermissions(PlatformConfig config)
    {
        var permissions = config.Gateway?.CrossWorldPermissions;
        if (permissions is null || permissions.Count == 0)
            return [];

        return permissions
            .Where(permission => !string.IsNullOrWhiteSpace(permission.TargetWorldId))
            .Select(permission => new CrossWorldPermission
            {
                TargetWorldId = permission.TargetWorldId!,
                AllowedAgents = permission.AllowedAgents?.Where(agent => !string.IsNullOrWhiteSpace(agent)).Select(AgentId.From).ToArray(),
                AllowInbound = permission.AllowInbound,
                AllowOutbound = permission.AllowOutbound
            })
            .OrderBy(permission => permission.TargetWorldId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return null;

        return property.GetString();
    }
}

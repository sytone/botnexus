using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Isolation;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration;

public sealed class DefaultLocationResolver : ILocationResolver
{
    private readonly IReadOnlyDictionary<string, Location>? _locations;
    private readonly IOptionsMonitor<PlatformConfig>? _platformConfig;
    private readonly IAgentRegistry? _agentRegistry;
    private readonly IReadOnlyList<IIsolationStrategy> _isolationStrategies;

    public DefaultLocationResolver(
        PlatformConfig platformConfig,
        IAgentRegistry? agentRegistry = null,
        IEnumerable<IIsolationStrategy>? isolationStrategies = null)
    {
        ArgumentNullException.ThrowIfNull(platformConfig);

        var worldDescriptor = WorldDescriptorBuilder.Build(platformConfig, agentRegistry, isolationStrategies);
        _locations = worldDescriptor.Locations.ToDictionary(location => location.Name, StringComparer.OrdinalIgnoreCase);
        _isolationStrategies = isolationStrategies?.ToArray() ?? [];
    }

    public DefaultLocationResolver(
        IOptionsMonitor<PlatformConfig> platformConfig,
        IAgentRegistry? agentRegistry = null,
        IEnumerable<IIsolationStrategy>? isolationStrategies = null)
    {
        ArgumentNullException.ThrowIfNull(platformConfig);
        _platformConfig = platformConfig;
        _agentRegistry = agentRegistry;
        _isolationStrategies = isolationStrategies?.ToArray() ?? [];
    }

    private IReadOnlyDictionary<string, Location> GetLocations()
    {
        if (_platformConfig is null)
            return _locations ?? new Dictionary<string, Location>(StringComparer.OrdinalIgnoreCase);

        var worldDescriptor = WorldDescriptorBuilder.Build(_platformConfig.CurrentValue, _agentRegistry, _isolationStrategies);
        return worldDescriptor.Locations.ToDictionary(location => location.Name, StringComparer.OrdinalIgnoreCase);
    }

    public Location? Resolve(string locationName)
    {
        if (string.IsNullOrWhiteSpace(locationName))
            return null;

        var locations = GetLocations();
        return locations.TryGetValue(locationName.Trim(), out var location)
            ? location
            : null;
    }

    public string? ResolvePath(string locationName)
    {
        var location = Resolve(locationName);
        if (location is null || location.Type != LocationType.FileSystem)
            return null;

        return location.Path;
    }

    public IReadOnlyList<Location> GetAll()
        => GetLocations().Values
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

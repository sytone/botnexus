using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Isolation;

namespace BotNexus.Gateway.Configuration;

public sealed class DefaultLocationResolver : ILocationResolver
{
    private readonly IReadOnlyDictionary<string, Location> _locations;

    public DefaultLocationResolver(
        PlatformConfig platformConfig,
        IAgentRegistry? agentRegistry = null,
        IEnumerable<IIsolationStrategy>? isolationStrategies = null)
    {
        ArgumentNullException.ThrowIfNull(platformConfig);

        var worldDescriptor = WorldDescriptorBuilder.Build(platformConfig, agentRegistry, isolationStrategies);
        _locations = worldDescriptor.Locations.ToDictionary(location => location.Name, StringComparer.OrdinalIgnoreCase);
    }

    public Location? Resolve(string locationName)
    {
        if (string.IsNullOrWhiteSpace(locationName))
            return null;

        return _locations.TryGetValue(locationName.Trim(), out var location)
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
        => _locations.Values
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

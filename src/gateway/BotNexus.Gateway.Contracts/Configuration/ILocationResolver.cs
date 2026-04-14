using BotNexus.Domain.World;

namespace BotNexus.Gateway.Abstractions.Configuration;

/// <summary>
/// Resolves named locations from the world descriptor location registry.
/// </summary>
public interface ILocationResolver
{
    /// <summary>
    /// Resolves a location by name.
    /// </summary>
    Location? Resolve(string locationName);

    /// <summary>
    /// Resolves a filesystem location path by name.
    /// </summary>
    string? ResolvePath(string locationName);

    /// <summary>
    /// Returns all registered locations.
    /// </summary>
    IReadOnlyList<Location> GetAll();
}

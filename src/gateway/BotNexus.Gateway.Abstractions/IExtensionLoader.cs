namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Discovers, loads, and unloads runtime extensions from an extensions directory.
/// </summary>
public interface IExtensionLoader
{
    Task<IReadOnlyList<ExtensionInfo>> DiscoverAsync(string extensionsPath, CancellationToken ct = default);
    Task<ExtensionLoadResult> LoadAsync(ExtensionInfo extension, CancellationToken ct = default);
    Task UnloadAsync(string extensionId, CancellationToken ct = default);
    IReadOnlyList<LoadedExtension> GetLoaded();
}

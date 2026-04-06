namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Manifest format stored in botnexus-extension.json.
/// </summary>
public sealed record ExtensionManifest
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string EntryAssembly { get; init; } = string.Empty;
    public IReadOnlyList<string> ExtensionTypes { get; init; } = [];
    public IReadOnlyList<string> Dependencies { get; init; } = [];
}

/// <summary>
/// Metadata for a discovered extension on disk.
/// </summary>
public sealed record ExtensionInfo
{
    public required string DirectoryPath { get; init; }
    public required string ManifestPath { get; init; }
    public required string EntryAssemblyPath { get; init; }
    public required ExtensionManifest Manifest { get; init; }
}

/// <summary>
/// Result of attempting to load an extension.
/// </summary>
public sealed record ExtensionLoadResult
{
    public required string ExtensionId { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> RegisteredServices { get; init; } = [];
}

/// <summary>
/// Runtime information about an extension that is currently loaded.
/// </summary>
public sealed record LoadedExtension
{
    public required string ExtensionId { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string DirectoryPath { get; init; }
    public required DateTimeOffset LoadedAtUtc { get; init; }
    public IReadOnlyList<string> RegisteredServices { get; init; } = [];
}

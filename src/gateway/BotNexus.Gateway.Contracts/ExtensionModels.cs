namespace BotNexus.Gateway.Abstractions.Extensions;
/// <summary>
/// Manifest format stored in botnexus-extension.json.
/// </summary>
public sealed record ExtensionManifest
{
    /// <summary>
    /// Gets or sets the id.
    /// </summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>
    /// Gets or sets the version.
    /// </summary>
    public string Version { get; init; } = string.Empty;
    /// <summary>
    /// Gets or sets the entry assembly.
    /// </summary>
    public string EntryAssembly { get; init; } = string.Empty;
    /// <summary>
    /// Gets or sets the extension types.
    /// </summary>
    public IReadOnlyList<string> ExtensionTypes { get; init; } = [];
    /// <summary>
    /// Gets or sets the dependencies.
    /// </summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    /// <summary>
    /// Whether this extension is enabled. When false, the extension is discovered but not loaded.
    /// Defaults to true.
    /// </summary>
    public bool Enabled { get; init; } = true;
    /// <summary>
    /// Configuration field schema declared by this extension.
    /// Used to validate operator config and apply defaults at startup.
    /// </summary>
    public IReadOnlyList<ExtensionConfigFieldSchema> ConfigSchema { get; init; } = [];
}
/// <summary>
/// Schema declaration for a single extension configuration field.
/// Extensions declare these in their botnexus-extension.json manifest so the
/// gateway can validate operator config and apply defaults at startup.
/// </summary>
public sealed record ExtensionConfigFieldSchema
{
    /// <summary>Field identifier (key in the extension config object).</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>Expected value type: string, integer, bool, object, array.</summary>
    public string Type { get; init; } = "string";
    /// <summary>Default value as a string. Applied when the field is absent and not required.</summary>
    public string? Default { get; init; }
    /// <summary>Whether this field must be present in operator config. Missing required fields produce a warning.</summary>
    public bool Required { get; init; }
    /// <summary>Whether this field contains a secret/credential (masked in logs and UI).</summary>
    public bool Sensitive { get; init; }
    /// <summary>Human-readable description of the field's purpose.</summary>
    public string? Description { get; init; }
}
/// <summary>
/// Metadata for a discovered extension on disk.
/// </summary>
public sealed record ExtensionInfo
{
    /// <summary>
    /// Gets or sets the directory path.
    /// </summary>
    public required string DirectoryPath { get; init; }
    /// <summary>
    /// Gets or sets the manifest path.
    /// </summary>
    public required string ManifestPath { get; init; }
    /// <summary>
    /// Gets or sets the entry assembly path.
    /// </summary>
    public required string EntryAssemblyPath { get; init; }
    /// <summary>
    /// Gets or sets the manifest.
    /// </summary>
    public required ExtensionManifest Manifest { get; init; }
}
/// <summary>
/// Result of attempting to load an extension.
/// </summary>
public sealed record ExtensionLoadResult
{
    /// <summary>
    /// Gets or sets the extension id.
    /// </summary>
    public required string ExtensionId { get; init; }
    /// <summary>
    /// Gets or sets the success.
    /// </summary>
    public required bool Success { get; init; }
    /// <summary>
    /// Gets or sets the error.
    /// </summary>
    public string? Error { get; init; }
    /// <summary>
    /// Gets or sets the registered services.
    /// </summary>
    public IReadOnlyList<string> RegisteredServices { get; init; } = [];
}
/// <summary>
/// Runtime information about an extension that is currently loaded.
/// </summary>
public sealed record LoadedExtension
{
    /// <summary>
    /// Gets or sets the extension id.
    /// </summary>
    public required string ExtensionId { get; init; }
    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public required string Name { get; init; }
    /// <summary>
    /// Gets or sets the version.
    /// </summary>
    public required string Version { get; init; }
    /// <summary>
    /// Gets or sets the directory path.
    /// </summary>
    public required string DirectoryPath { get; init; }
    /// <summary>
    /// Gets or sets the entry assembly path.
    /// </summary>
    public required string EntryAssemblyPath { get; init; }
    /// <summary>
    /// Gets or sets the extension types.
    /// </summary>
    public IReadOnlyList<string> ExtensionTypes { get; init; } = [];
    /// <summary>
    /// Gets or sets the loaded at utc.
    /// </summary>
    public required DateTimeOffset LoadedAtUtc { get; init; }
    /// <summary>
    /// Gets or sets the registered services.
    /// </summary>
    public IReadOnlyList<string> RegisteredServices { get; init; } = [];
    /// <summary>
    /// Whether this extension is enabled. Sourced from the manifest.
    /// </summary>
    public bool Enabled { get; init; } = true;
    /// <summary>
    /// Configuration field schema declared by this extension in the manifest.
    /// </summary>
    public IReadOnlyList<ExtensionConfigFieldSchema> ConfigSchema { get; init; } = [];
}

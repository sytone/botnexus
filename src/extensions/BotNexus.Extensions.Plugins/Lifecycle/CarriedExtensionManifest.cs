using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// The fields of a carried <c>botnexus-extension.json</c> that deployment needs, and no others.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately a local projection rather than a reference to the gateway's own
/// <c>ExtensionManifest</c>. The plugin domain deploys an opaque directory; it does not load
/// assemblies, resolve contributors, or interpret <c>extensionTypes</c>. Referencing
/// <c>BotNexus.Gateway.Contracts</c> to read two strings would point the plugin domain at gateway
/// internals it otherwise has no business knowing, and this project deliberately carries only a
/// scheduler reference today.
/// </para>
/// <para>
/// Unknown fields are ignored rather than rejected: the authority on the extension manifest's full
/// shape is the gateway loader, which validates it at startup. Duplicating that validation here
/// would create two contracts that drift.
/// </para>
/// </remarks>
public sealed record CarriedExtensionManifest
{
    /// <summary>
    /// Extension identifier. Becomes the deployed directory name under the extensions root, so it
    /// is validated as a safe single path segment before use.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// File name of the entry assembly, relative to the extension manifest's own directory.
    /// Checked for existence at deploy time so a plugin that forgot to commit its build output
    /// fails at install with a clear message rather than at the next gateway start.
    /// </summary>
    [JsonPropertyName("entryAssembly")]
    public string EntryAssembly { get; init; } = string.Empty;

    /// <summary>Display name, used when telling an operator what they are about to install.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Contracts the extension implements - tools, channels, endpoints. Read only to DISCLOSE
    /// them; the gateway loader remains the authority on what is actually activated.
    /// </summary>
    [JsonPropertyName("extensionTypes")]
    public IReadOnlyList<string>? ExtensionTypes { get; init; }

    /// <summary>Left-nav entries the extension declares, disclosed as paths it will serve.</summary>
    [JsonPropertyName("nav")]
    public IReadOnlyList<CarriedExtensionNav>? Nav { get; init; }

    /// <summary>
    /// Where the extension asks to map in the request pipeline. A carried extension must declare
    /// <c>after-authentication</c>; see <c>PluginExtensionDeployer</c> for why.
    /// </summary>
    [JsonPropertyName("endpointPhase")]
    public string? EndpointPhase { get; init; }
}

/// <summary>The parts of a carried nav entry worth showing an operator before they consent.</summary>
public sealed record CarriedExtensionNav
{
    /// <summary>Sidebar text.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>Path the entry will serve.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }
}

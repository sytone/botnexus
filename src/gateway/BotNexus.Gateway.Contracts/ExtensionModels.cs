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

    /// <summary>
    /// Left-nav entries this extension contributes to the portal. Empty for an extension with no
    /// UI, which is most of them.
    /// </summary>
    /// <remarks>
    /// Declared on the EXTENSION manifest rather than a plugin manifest so that an extension built
    /// from source gets contributed nav on the same terms as one delivered by a marketplace
    /// plugin. Nav is a property of the thing that serves the path, not of the thing that
    /// delivered it.
    /// </remarks>
    public IReadOnlyList<ExtensionNavEntry> Nav { get; init; } = [];

    /// <summary>
    /// Gateway contract range this extension was built against, or <c>null</c> to declare no
    /// constraint.
    /// </summary>
    /// <remarks>
    /// Absent means unconstrained, so every extension written before this field keeps loading
    /// exactly as it did. Declaring a range is how a PREBUILT extension - one delivered by a
    /// marketplace plugin, which was compiled somewhere else against some other gateway - says
    /// which gateways it is safe on.
    /// </remarks>
    public ExtensionCompatibility? Compatibility { get; init; }

    /// <summary>
    /// Where this extension's endpoints and middleware map in the request pipeline.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="ExtensionEndpointPhase.BeforeAuthentication"/>, which is where every
    /// contributor has always mapped. The default is deliberately the existing behaviour rather
    /// than the safer value: changing it wholesale would move the portal's own contributor - which
    /// serves the shell and the auth interstitial - behind the authentication it exists to let a
    /// user get through.
    /// <para>
    /// Third-party code does not get that default by accident. A plugin-carried extension must
    /// declare <see cref="ExtensionEndpointPhase.AfterAuthentication"/>, and the installer refuses
    /// it otherwise, so mapping ahead of authentication is an explicit, acknowledged choice.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// A STRING on the wire, parsed leniently, rather than a JSON enum. The manifest deserialiser
    /// carries no string-enum converter, so a typed enum here would throw on the very first
    /// manifest that wrote a human-readable value - taking the whole manifest, and therefore the
    /// whole extension, down over a presentational field.
    /// </remarks>
    public string? EndpointPhase { get; init; }

    /// <summary>The parsed phase, defaulting to before-authentication.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ExtensionEndpointPhase ResolvedEndpointPhase => ParseEndpointPhase(EndpointPhase);

    /// <summary>
    /// Parses a declared phase. Accepts <c>after-authentication</c>, <c>afterauthentication</c> and
    /// <c>AfterAuthentication</c>; anything else - including null and nonsense - means
    /// before-authentication, which is where contributors have always mapped.
    /// </summary>
    /// <param name="value">Declared value, or <c>null</c>.</param>
    public static ExtensionEndpointPhase ParseEndpointPhase(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ExtensionEndpointPhase.BeforeAuthentication
            : value.Trim().Replace("-", string.Empty, StringComparison.Ordinal)
                .Equals("afterauthentication", StringComparison.OrdinalIgnoreCase)
                ? ExtensionEndpointPhase.AfterAuthentication
                : ExtensionEndpointPhase.BeforeAuthentication;
}

/// <summary>Where an extension's endpoints map relative to the authentication middleware.</summary>
public enum ExtensionEndpointPhase
{
    /// <summary>
    /// Ahead of authentication. Required by surfaces that must be reachable to authenticate at
    /// all, such as the portal shell.
    /// </summary>
    BeforeAuthentication = 0,

    /// <summary>
    /// Behind authentication, so the extension's routes are subject to the gateway's auth and its
    /// origin checks like any other API route.
    /// </summary>
    AfterAuthentication = 1,
}

/// <summary>
/// The range of gateway contract versions an extension supports.
/// </summary>
/// <remarks>
/// Expressed against <c>BotNexus.Gateway.Abstractions</c> - the assembly whose types an extension
/// actually binds to - rather than a product version, because that is the contract that breaks.
/// Both bounds are optional and each is checked independently, so an extension can declare a floor
/// without committing to a ceiling.
/// </remarks>
public sealed record ExtensionCompatibility
{
    /// <summary>
    /// Lowest supported Abstractions version, INCLUSIVE. <c>null</c> means no lower bound.
    /// </summary>
    public string? MinAbstractionsVersion { get; init; }

    /// <summary>
    /// First Abstractions version that is NOT supported - an exclusive upper bound, so
    /// <c>"1.0.0"</c> means "everything below 1.0.0". <c>null</c> means no upper bound.
    /// </summary>
    /// <remarks>
    /// Exclusive rather than inclusive because a ceiling is nearly always "up to the next breaking
    /// release", and an inclusive bound forces authors to write the largest version they can
    /// imagine instead of the one they know breaks.
    /// </remarks>
    public string? MaxAbstractionsVersion { get; init; }
}

/// <summary>
/// One left-nav entry contributed by an extension.
/// </summary>
public sealed record ExtensionNavEntry
{
    /// <summary>Stable key for ordering and de-duplication, e.g. <c>agent-builder</c>.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Text shown in the sidebar.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Path the entry navigates to. Must be a site-relative path beginning with <c>/</c>; anything
    /// else is dropped rather than rendered.
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// Name of a portal icon. Unknown names fall back to a default rather than failing, and
    /// arbitrary markup is never accepted - an extension cannot inject SVG into the portal DOM.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>Sort position among nav entries; lower sorts earlier.</summary>
    public int Order { get; init; }

    /// <summary>
    /// Whether the path is served outside the Blazor router and therefore cannot be reached by
    /// client-side routing.
    /// </summary>
    public bool External { get; init; }

    /// <summary>
    /// Whether the view should replace the whole window instead of being hosted inside the portal.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>false</c>, so a contributed view is embedded in the portal frame and keeps
    /// the sidebar, header and theme around it. Navigating away from the portal entirely is the
    /// disjointed option and therefore the one that has to be asked for, not the default a plugin
    /// gets by saying nothing.
    /// </remarks>
    public bool FullPage { get; init; }
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

    /// <summary>Left-nav entries this extension contributes to the portal.</summary>
    public IReadOnlyList<ExtensionNavEntry> Nav { get; init; } = [];

    /// <summary>Where this extension's endpoints map in the request pipeline.</summary>
    public ExtensionEndpointPhase EndpointPhase { get; init; } = ExtensionEndpointPhase.BeforeAuthentication;

    /// <summary>
    /// Full type names of the services this extension registered, used to attribute a contributor
    /// instance back to the extension that supplied it.
    /// </summary>
    public IReadOnlyList<string> RegisteredImplementationTypes { get; init; } = [];
}

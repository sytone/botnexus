using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// A git repository the operator has added as a place to find plugins.
/// </summary>
/// <remarks>
/// A source is a place to LOOK, not something installed. Adding one costs nothing and installs
/// nothing: it makes the plugins it offers visible so a person can choose. That separation is the
/// point of the registry - before it, the only way to learn what a repository offered was to
/// install it and find out.
/// </remarks>
public sealed record MarketplaceSource
{
    /// <summary>Stable identifier, lowercase kebab-case, derived from the URL when not supplied.</summary>
    public required string Name { get; init; }

    /// <summary>Git URL of the repository to look in.</summary>
    public required string Url { get; init; }

    /// <summary>Branch, tag or commit to read. Null means the repository's default branch.</summary>
    public string? Reference { get; init; }

    /// <summary>When the operator added it.</summary>
    public DateTimeOffset AddedAtUtc { get; init; }

    /// <summary>
    /// When its contents were last read successfully, or null if never.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="LastError"/> on purpose: a source that refreshed yesterday and
    /// failed today should show both, because "stale" and "broken" call for different reactions.
    /// </remarks>
    public DateTimeOffset? LastRefreshedAtUtc { get; init; }

    /// <summary>Why the last refresh failed, or null when it succeeded.</summary>
    public string? LastError { get; init; }

    /// <summary>
    /// What the source turned out to be: <c>catalog</c> for a repository carrying
    /// marketplace.json, <c>plugin</c> for one that is itself a plugin, or null until read.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>Plugins this source offers, as of the last successful refresh.</summary>
    [JsonPropertyName("offerings")]
    public IReadOnlyList<MarketplaceOffering> Offerings { get; init; } = [];
}

/// <summary>
/// One plugin a source offers, as read from its manifest.
/// </summary>
/// <remarks>
/// Read from the repository rather than restated by the catalogue that lists it. A catalogue entry
/// is a claim; the manifest is what the installer will actually act on, and the two can disagree.
/// <see cref="CarriesExtension"/> in particular decides whether installing runs third-party code
/// in the gateway, which is not something a listing should take anyone's word for.
/// </remarks>
public sealed record MarketplaceOffering
{
    /// <summary>Plugin name, as the installer will record it.</summary>
    public required string Name { get; init; }

    /// <summary>Git URL to install from. For a catalogue this is the entry's own repository.</summary>
    public required string Url { get; init; }

    /// <summary>Version from the plugin manifest, when it could be read.</summary>
    public string? Version { get; init; }

    /// <summary>One-line summary for the listing.</summary>
    public string? Description { get; init; }

    /// <summary>Reference to install: a tag or commit when the source pins one.</summary>
    public string? Reference { get; init; }

    /// <summary>
    /// Whether installing this runs third-party code in the gateway. Surfaced in the listing so
    /// the choice is informed before the consent prompt, not at it.
    /// </summary>
    public bool CarriesExtension { get; init; }

    /// <summary>Why this entry could not be read, when it could not.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// A problem with the catalog's pinned <c>version</c> itself, or <c>null</c> when it is fine.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Error"/>: the entry WAS read, so it still lists and installs. The
    /// warning says the catalog points somewhere stale or nonexistent, which matters because a
    /// catalog version is the git ref an install resolves - a pin left behind installs the previous
    /// release, silently, with nothing else to reveal it.
    /// </remarks>
    public string? VersionWarning { get; init; }
}

using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// The record of one installed plugin. This is the authority on what install materialised;
/// removal and update both work from <see cref="Files"/> rather than from whatever happens to
/// be in the directory at the time, so content a user added alongside a plugin is never
/// collateral damage.
/// </summary>
public sealed record InstalledPlugin
{
    /// <summary>Plugin identifier, taken from the plugin's own manifest.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Marketplace source the content was fetched from.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>
    /// Branch, tag or commit requested at install time, or <c>null</c> for the default branch.
    /// Kept separate from <see cref="ResolvedVersion"/>: the request is what update re-resolves,
    /// the resolution is what is currently on disk.
    /// </summary>
    [JsonPropertyName("reference")]
    public string? Reference { get; init; }

    /// <summary>
    /// Exact revision currently materialised on disk - a commit SHA for a git source. Recorded
    /// so an update can report whether the source actually moved.
    /// </summary>
    [JsonPropertyName("resolvedVersion")]
    public required string ResolvedVersion { get; init; }

    /// <summary>Version string the plugin's own manifest advertised, or <c>null</c> if unversioned.</summary>
    [JsonPropertyName("manifestVersion")]
    public string? ManifestVersion { get; init; }

    /// <summary>
    /// Whether update may replace this plugin's content. Defaults to <c>true</c> - the settled
    /// decision in #2623 is that pinning is opt-in, so a plugin installed without an explicit
    /// preference tracks its source.
    /// </summary>
    [JsonPropertyName("updatesEnabled")]
    public bool UpdatesEnabled { get; init; } = true;

    /// <summary>When the current content was materialised.</summary>
    [JsonPropertyName("installedAtUtc")]
    public required DateTimeOffset InstalledAtUtc { get; init; }

    /// <summary>
    /// Every file install wrote, as forward-slash paths relative to the plugin directory. This
    /// is the exact-set removal manifest; it is never re-derived by scanning the directory.
    /// </summary>
    [JsonPropertyName("files")]
    public IReadOnlyList<string> Files { get; init; } = [];
}

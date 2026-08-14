namespace BotNexus.Extensions.BrowserTools;

/// <summary>
/// A single pinned <c>agent-browser</c> release asset: where to fetch it and what it must hash to.
/// </summary>
/// <param name="Version">Exact release version. Never a range, never <c>latest</c>.</param>
/// <param name="RuntimeIdentifier">Platform RID the asset is built for, e.g. <c>win-x64</c>.</param>
/// <param name="AssetUrl">Absolute URL of the release asset.</param>
/// <param name="Sha256">Lowercase hex sha256 the downloaded bytes MUST hash to.</param>
public sealed record AgentBrowserReleaseAsset(
    string Version,
    string RuntimeIdentifier,
    string AssetUrl,
    string Sha256);

/// <summary>
/// The set of release assets this build is willing to download (#3029 AC8).
/// </summary>
/// <remarks>
/// <para>
/// Provisioning is gated on a catalogue lookup rather than on a URL built from the configured
/// version string. Deriving the URL from config would let an operator point auto-provision at an
/// arbitrary version whose digest nobody has ever pinned, and a digest check against an unknown
/// expected value is not a check at all.
/// </para>
/// <para>
/// The default catalogue is intentionally EMPTY. Until a specific build of <c>agent-browser</c>
/// has been reviewed and its sha256 recorded here, auto-provision fails closed with an actionable
/// message rather than fetching-and-trusting. Populating this table is a deliberate, reviewable
/// act; a placeholder digest committed to make a test go green would defeat the entire control.
/// </para>
/// </remarks>
public sealed class AgentBrowserReleaseCatalog
{
    private readonly IReadOnlyList<AgentBrowserReleaseAsset> _assets;

    /// <summary>Creates a catalogue over the supplied pinned assets.</summary>
    public AgentBrowserReleaseCatalog(IReadOnlyList<AgentBrowserReleaseAsset>? assets = null)
        => _assets = assets ?? [];

    /// <summary>The catalogue shipped with this build. Empty until an asset is pinned.</summary>
    public static AgentBrowserReleaseCatalog Default { get; } = new([]);

    /// <summary>Finds the pinned asset for a version and RID, or <c>null</c> when none is pinned.</summary>
    public AgentBrowserReleaseAsset? Find(string version, string runtimeIdentifier)
        => _assets.FirstOrDefault(a =>
            string.Equals(a.Version, version, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.RuntimeIdentifier, runtimeIdentifier, StringComparison.OrdinalIgnoreCase));
}

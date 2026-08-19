using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Portal;

/// <summary>
/// Builds the portal's view of installed plugins from the installed-plugin records plus what is
/// actually on disk.
/// </summary>
/// <remarks>
/// This type deliberately does NOT own a second state model. <see cref="PluginStateStore"/>
/// remains the authority on what is installed; everything here is derived from it. Inventing a
/// parallel record would guarantee the two drift, and the installed record is the only thing
/// that knows which files a plugin owns.
/// <para>
/// Update availability is derived WITHOUT a network call by default. Probing every source on
/// every page render would make a list view cost N git clones, and the answer would still be
/// stale by the time it rendered. <see cref="PluginUpdateState.Unknown"/> is therefore the honest
/// default, and a caller that wants a real answer asks for it explicitly.
/// </para>
/// </remarks>
public sealed class PluginPortalProjector
{
    private readonly PluginStateStore _store;

    /// <summary>Creates a projector over an installed-plugin record store.</summary>
    /// <param name="store">Record store, which also defines the plugin root.</param>
    public PluginPortalProjector(PluginStateStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Projects every installed plugin, ordered by name so the rendered row order is stable
    /// across reloads rather than following the state file's write order.
    /// </summary>
    public IReadOnlyList<PluginPortalRow> List() =>
        _store.Read()
            .OrderBy(static p => p.Name, StringComparer.Ordinal)
            .Select(Project)
            .ToList();

    /// <summary>Projects one plugin by name, or <c>null</c> when it is not installed.</summary>
    /// <param name="name">Plugin identifier.</param>
    public PluginPortalRow? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var record = _store.Find(name);
        return record is null ? null : Project(record);
    }

    /// <summary>
    /// Projects one installed record, deriving its trust state from the files actually present
    /// against the exact set install recorded.
    /// </summary>
    /// <param name="plugin">Installed record.</param>
    public PluginPortalRow Project(InstalledPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        var (trust, detail) = EvaluateTrust(plugin);

        return new PluginPortalRow
        {
            Name = plugin.Name,
            Source = plugin.Source,
            Reference = plugin.Reference,
            ResolvedVersion = plugin.ResolvedVersion,
            ManifestVersion = plugin.ManifestVersion,
            UpdatesEnabled = plugin.UpdatesEnabled,
            InstalledAtUtc = plugin.InstalledAtUtc,
            FileCount = plugin.Files.Count,
            TrustState = trust,
            TrustDetail = detail,
            // A pinned plugin's source is never probed, so "pinned" is the complete and final
            // answer to the update question for it - not a placeholder for an unmade check.
            UpdateState = plugin.UpdatesEnabled ? PluginUpdateState.Unknown : PluginUpdateState.Pinned,
        };
    }

    // Integrity is judged against the recorded file set, never against a directory scan: a file
    // the user dropped alongside plugin content is not a modification of the plugin, and treating
    // it as one would cry wolf on exactly the content removal is careful to preserve.
    private (PluginTrustState State, string? Detail) EvaluateTrust(InstalledPlugin plugin)
    {
        // An install that recorded no files cannot be attested either way. Reporting Verified
        // here would attest an empty claim.
        if (plugin.Files.Count == 0)
        {
            return (PluginTrustState.Unverified, "No installed file record; integrity cannot be attested.");
        }

        var directory = Path.Combine(_store.PluginRoot, plugin.Name);
        if (!Directory.Exists(directory))
        {
            return (PluginTrustState.Modified, "The plugin directory is recorded as installed but is missing from disk.");
        }

        var missing = plugin.Files
            .Where(relative => !File.Exists(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();

        if (missing.Count > 0)
        {
            var named = string.Join(", ", missing.Take(3));
            var suffix = missing.Count > 3 ? $" (and {missing.Count - 3} more)" : string.Empty;
            return (PluginTrustState.Modified, $"{missing.Count} recorded file(s) are missing: {named}{suffix}.");
        }

        // Presence is all this slice can attest. Content hashing arrives with the install-time
        // trust catalog (#2682); claiming Verified on presence alone would overstate it.
        return (PluginTrustState.Unverified, "All recorded files are present. Content hashes are not yet catalogued.");
    }
}

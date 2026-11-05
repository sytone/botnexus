using BotNexus.Extensions.Plugins;
using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Mcp.Plugins;

/// <summary>
/// Explicit operator-configured standalone package boundary. Its root must be disjoint from
/// the configured installed root. Filesystem shape or absent installed state cannot create authority.
/// </summary>
public sealed class NativePluginMcpAuthority
{
    /// <summary>Creates a standalone boundary tied to the configured installed-plugin boundary.</summary>
    /// <param name="installedStore">Authoritative installed store, even when its state is unavailable.</param>
    /// <param name="standaloneRoot">Explicit standalone package root; may not overlap the installed root.</param>
    public NativePluginMcpAuthority(PluginStateStore installedStore, string standaloneRoot)
    {
        ArgumentNullException.ThrowIfNull(installedStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(standaloneRoot);
        InstalledRoot = Canonical(installedStore.PluginRoot);
        StandaloneRoot = Canonical(standaloneRoot);
        EnsureDisjoint();
        ValidateInstalledAncestry();
    }

    /// <summary>Configured standalone package root, not inferred from a package path.</summary>
    public string StandaloneRoot { get; }

    /// <summary>Configured installed boundary, independent of state-file presence or contents.</summary>
    public string InstalledRoot { get; }

    internal bool Matches(PluginStateStore store) =>
        string.Equals(InstalledRoot, Canonical(store.PluginRoot), Comparison);

    internal void Validate(string directory)
    {
        EnsureDisjoint();
        ValidateInstalledAncestry();
        var package = Canonical(directory);
        if (!IsWithin(StandaloneRoot, package) || IsWithin(InstalledRoot, package))
            throw new InvalidDataException("Standalone content is outside its configured authority or inside installed ownership.");

        // Inspect ancestry for deny-only provenance. Never search for an ancestor record that
        // grants execution. Missing installed state does not affect the configured boundary above.
        for (var current = package; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Standalone authority does not accept linked ancestry.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var name = Path.GetFileName(entry);
                if (name.Equals(PluginStateStore.StateFileName, StringComparison.OrdinalIgnoreCase)
                    || name.Equals("plugin.json", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(".claude-plugin", StringComparison.OrdinalIgnoreCase)
                    || (!string.Equals(current, package, Comparison)
                        && name.Equals(PluginManifestParser.PluginMetadataDirectoryName, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Standalone content has foreign or installed package ancestry.");
            }
        }
    }

    private void ValidateInstalledAncestry()
    {
        // A missing root is allowed as configuration, but an existing alias cannot disguise
        // overlap with the standalone root. Do not inspect or trust installed state contents.
        for (var current = InstalledRoot; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Configured installed ancestry must not contain filesystem links.");
        }
    }

    private void EnsureDisjoint()
    {
        if (IsWithin(InstalledRoot, StandaloneRoot) || IsWithin(StandaloneRoot, InstalledRoot))
            throw new InvalidDataException("Standalone and installed authority roots must be disjoint.");
    }

    private static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsWithin(string root, string path) => string.Equals(root, path, Comparison)
        || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, Comparison);
}

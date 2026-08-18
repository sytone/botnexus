using System.IO.Abstractions;

namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// Resolves the skills directory of every installed plugin, so skill discovery can merge
/// plugin-shipped skills without knowing anything about plugin storage layout.
/// </summary>
/// <remarks>
/// <para>
/// <b>The installed record is the authority, not the directory listing.</b> Roots are derived from
/// <see cref="PluginStateStore.Read"/> rather than by enumerating subdirectories of the plugin root.
/// <see cref="PluginLifecycleManager"/> already refuses to install over a directory that exists but
/// is not recorded, precisely because an unrecorded directory has no known provenance and no removal
/// manifest. Surfacing skills out of such a directory would hand agent context to content that
/// nothing claims to have installed - a trivial way to smuggle a skill onto a machine by dropping a
/// folder next to real plugins.
/// </para>
/// <para>
/// This type deliberately returns plain directory paths rather than skill definitions. Parsing,
/// validation, security scanning and trust verification all already live in
/// <c>SkillDiscovery</c>, and a second discovery implementation for plugins is exactly how the
/// enforced set and the surfaced set drift apart.
/// </para>
/// </remarks>
public static class PluginSkillRootResolver
{
    /// <summary>
    /// Directory name, relative to a plugin's own directory, holding that plugin's skills. Matches
    /// the by-convention layout documented in the plugin manifest contract.
    /// </summary>
    public const string SkillsDirectoryName = "skills";

    /// <summary>
    /// Directory name, relative to the BotNexus home directory, that holds installed plugins.
    /// Named here so composition roots do not each re-spell the path.
    /// </summary>
    public const string PluginRootDirectoryName = "plugins";

    /// <summary>
    /// Returns the skills directory of each installed plugin that actually has one, ordered by
    /// plugin name so discovery results are stable across runs.
    /// </summary>
    /// <param name="store">Installed-plugin record store, which also defines the plugin root.</param>
    /// <param name="fileSystem">Filesystem abstraction; defaults to the real filesystem.</param>
    /// <returns>
    /// Absolute skills directory paths. Empty when nothing is installed, which is the correct
    /// reading of "this machine has no plugin skills" and keeps discovery byte-identical to its
    /// pre-plugin behaviour.
    /// </returns>
    public static IReadOnlyList<string> Resolve(PluginStateStore store, IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var fs = fileSystem ?? new FileSystem();

        var roots = new List<string>();

        foreach (var plugin in store.Read().OrderBy(static p => p.Name, StringComparer.Ordinal))
        {
            var skillsDir = fs.Path.Combine(store.PluginRoot, plugin.Name, SkillsDirectoryName);
            if (fs.Directory.Exists(skillsDir))
            {
                roots.Add(skillsDir);
            }
        }

        return roots;
    }

    /// <summary>
    /// Convenience overload resolving the skills directories under a plugin root path.
    /// </summary>
    /// <param name="pluginRoot">Directory holding installed plugins; null or absent yields no roots.</param>
    /// <param name="fileSystem">Filesystem abstraction; defaults to the real filesystem.</param>
    public static IReadOnlyList<string> Resolve(string? pluginRoot, IFileSystem? fileSystem = null)
    {
        if (string.IsNullOrWhiteSpace(pluginRoot))
        {
            return [];
        }

        var fs = fileSystem ?? new FileSystem();

        // A plugin root that was never created is the overwhelmingly common case on a machine with
        // no plugins installed. Returning early keeps that path free of an exception-driven read.
        if (!fs.Directory.Exists(pluginRoot))
        {
            return [];
        }

        return Resolve(new PluginStateStore(pluginRoot, fs), fs);
    }
}

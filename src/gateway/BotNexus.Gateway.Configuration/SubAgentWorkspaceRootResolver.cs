using System.IO.Abstractions;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Single source of truth for the sub-agent temporary workspace root directory. Both the gateway
/// (<c>FileAgentWorkspaceManager</c>, when it creates and reclaims a sub-agent's isolated workspace)
/// and the CLI (<c>subagent workspace list|prune</c> and the <c>doctor</c> reconciliation check)
/// resolve the root through this one helper so the two can never drift apart - a divergence would
/// silently leave sub-agent workspace directories unreaped (issue #2040).
/// <para>
/// When no override is configured the historical default is preserved exactly:
/// <c>&lt;Path.GetTempPath()&gt;/botnexus-subagent-workspaces</c>, so existing installs are unchanged.
/// A configured override (<c>gateway.subAgents.workspaceRoot</c>) is expanded consistently with other
/// BotNexus path settings - a leading <c>~</c> maps to the user home directory and
/// <c>%VAR%</c>-style environment references are expanded - then normalized to an absolute path.
/// </para>
/// </summary>
public static class SubAgentWorkspaceRootResolver
{
    /// <summary>
    /// The historical directory name created under the OS temp root. Retained as the default leaf so
    /// installs that never set an override keep using exactly the same location.
    /// </summary>
    public const string DefaultDirectoryName = "botnexus-subagent-workspaces";

    /// <summary>
    /// Resolves the absolute sub-agent workspace root. When <paramref name="configuredRoot"/> is null
    /// or whitespace the default (<c>&lt;temp&gt;/botnexus-subagent-workspaces</c>) is returned,
    /// preserving pre-#2040 behaviour. Otherwise the configured value has <c>~</c> and environment
    /// variables expanded and is normalized to an absolute path.
    /// </summary>
    /// <param name="configuredRoot">The optional configured override (<c>subAgents.workspaceRoot</c>).</param>
    /// <param name="fileSystem">Filesystem abstraction used for temp-root and path operations.</param>
    /// <returns>An absolute, normalized workspace root path.</returns>
    public static string Resolve(string? configuredRoot, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (string.IsNullOrWhiteSpace(configuredRoot))
            return fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), DefaultDirectoryName);

        var expanded = ExpandUserHome(Environment.ExpandEnvironmentVariables(configuredRoot.Trim()));
        return fileSystem.Path.GetFullPath(expanded);
    }

    /// <summary>
    /// Expands a leading <c>~</c> (optionally followed by a separator) to the current user's home
    /// directory, mirroring the expansion used by other BotNexus path settings
    /// (<see cref="BotNexus.Gateway.Security.DefaultPathValidator"/>). A bare <c>~</c> maps to the
    /// home directory; anything else is returned unchanged.
    /// </summary>
    private static string ExpandUserHome(string path)
    {
        if (!path.StartsWith('~'))
            return path;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;

        if (path.Length == 1)
            return home;

        var first = path[1];
        if (first == Path.DirectorySeparatorChar || first == Path.AltDirectorySeparatorChar)
            return Path.Combine(home, path[2..]);

        return path;
    }
}

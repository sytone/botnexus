using System.IO.Abstractions;
using BotNexus.Domain.Paths;

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

        var expanded = HomePathExpander.Expand(Environment.ExpandEnvironmentVariables(configuredRoot.Trim()));
        return fileSystem.Path.GetFullPath(expanded);
    }

}

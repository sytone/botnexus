using System.IO.Abstractions;
using BotNexus.Extensions.Plugins.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// Applies a <see cref="ContentTrustMode"/> to an installed plugin's materialised content.
/// </summary>
/// <remarks>
/// <para>
/// The gate separates the two questions a trust decision actually contains: <i>did the content
/// change</i> (a fact, computed by <see cref="ContentTrustCatalog"/>) and <i>what should happen
/// about it</i> (a policy, held here). Fusing them is how a Warn deployment ends up silently
/// enforcing, or an Enforce deployment ends up silently warning.
/// </para>
/// <para>
/// <b>Every mode logs; only Enforce refuses.</b> Warn exists to make a tamper visible on a fleet
/// that cannot yet afford to fail closed, so a Warn that stayed silent would be indistinguishable
/// from Disabled and worth nothing. Disabled does not verify at all - it is the legacy posture and
/// must cost nothing, because hashing every file of every plugin on every check is not free.
/// </para>
/// </remarks>
public sealed class PluginTrustGate
{
    private readonly ContentTrustMode _mode;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;

    /// <summary>Creates a gate in a given posture.</summary>
    /// <param name="mode">Trust posture to apply.</param>
    /// <param name="fileSystem">File system abstraction; defaults to the real filesystem.</param>
    /// <param name="logger">Logger; optional.</param>
    public PluginTrustGate(
        ContentTrustMode mode,
        IFileSystem? fileSystem = null,
        ILogger<PluginTrustGate>? logger = null)
    {
        _mode = mode;
        _fileSystem = fileSystem ?? new FileSystem();
        _logger = logger ?? NullLogger<PluginTrustGate>.Instance;
    }

    /// <summary>The posture this gate applies.</summary>
    public ContentTrustMode Mode => _mode;

    /// <summary>
    /// Decides whether <paramref name="pluginName"/>'s content may be used.
    /// </summary>
    /// <param name="pluginName">Plugin identifier, used only for the log record.</param>
    /// <param name="pluginDirectory">Absolute directory holding the plugin's materialised content.</param>
    /// <returns>
    /// True when the content may be used. Under <see cref="ContentTrustMode.Warn"/> this is true
    /// even for content that failed verification - the violation is on the record either way.
    /// </returns>
    public bool Allow(string pluginName, string pluginDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        if (_mode == ContentTrustMode.Disabled)
        {
            return true;
        }

        var result = Verify(pluginDirectory);
        if (result.Trusted)
        {
            return true;
        }

        var violations = string.Join("; ", result.Violations);

        if (_mode == ContentTrustMode.Enforce)
        {
            _logger.LogError(
                "Refused plugin {Plugin}: its content does not match the trust catalog recorded at install time. Violations: {Violations}",
                pluginName,
                violations);
            return false;
        }

        _logger.LogWarning(
            "Plugin {Plugin} does not match the trust catalog recorded at install time, but trust mode is Warn so it is permitted. Violations: {Violations}",
            pluginName,
            violations);
        return true;
    }

    /// <summary>
    /// Verifies a plugin directory against its catalog, reporting unlisted files as violations.
    /// </summary>
    /// <param name="pluginDirectory">Absolute directory holding the plugin's materialised content.</param>
    /// <remarks>
    /// Unlisted-file detection is ON for plugins and off for skills. A plugin's catalog is
    /// generated over exactly what install materialised (#2682 AC5), so a file that appears
    /// afterwards was not installed by the platform and is a modification of the plugin - treating
    /// it as invisible would make the catalog a claim about an unnamed subset.
    /// </remarks>
    public TrustVerificationResult Verify(string pluginDirectory) =>
        ContentTrustCatalog.Verify(
            pluginDirectory,
            _fileSystem,
            ContentTrustCatalog.IncludeEveryFile,
            detectUnlistedFiles: true,
            includeDirectory: ContentTrustCatalog.IncludeEveryDirectory);
}

using BotNexus.Gateway.Abstractions.Extensions;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Captures the outcome of the startup extension-load pass so it can be surfaced by the
/// gateway API after the host is built.
///
/// Boot-time extension loading (see <c>Program.cs</c> /
/// <c>ServiceCollectionExtensions.LoadConfiguredExtensionsAsync</c>) happens before the
/// DI container is built, so a failing extension only emitted a bootstrap warning and was
/// otherwise invisible: <c>/health</c> stayed green and the CLI surfaced only a generic
/// health-check timeout. That masked every extension-assembly-load regression until it hit
/// production. This report is populated with the raw <see cref="ExtensionLoadResult"/> set
/// the moment loading completes and registered as a singleton, letting
/// <c>GET /api/extensions/health</c> report the actual per-extension load error (naming the
/// missing or diverged assembly). See issue #2220.
/// </summary>
public sealed class ExtensionBootReport
{
    /// <summary>
    /// Gets the load results captured during startup, one per extension that was attempted.
    /// Empty when extension loading was disabled or no extensions were discovered.
    /// </summary>
    public IReadOnlyList<ExtensionLoadResult> Results { get; private set; } = [];

    /// <summary>
    /// Gets whether every attempted extension loaded successfully. True when no extension
    /// was attempted (nothing to fail) and true when all attempts succeeded.
    /// </summary>
    public bool AllSucceeded => Results.All(result => result.Success);

    /// <summary>
    /// Records the results of the startup extension-load pass. Called once from the boot
    /// path immediately after <c>LoadConfiguredExtensionsAsync</c> completes.
    /// </summary>
    /// <param name="results">The load results, one per attempted extension.</param>
    public void Record(IReadOnlyList<ExtensionLoadResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        Results = results;
    }
}

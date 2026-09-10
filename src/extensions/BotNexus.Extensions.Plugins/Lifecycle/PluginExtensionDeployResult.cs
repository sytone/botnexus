namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// Outcome of deploying a plugin's carried extension.
/// </summary>
/// <remarks>
/// Failures are returned rather than thrown so an install can roll back the plugin it just
/// promoted and report which manifest field was wrong, instead of leaving a half-installed plugin
/// behind an exception.
/// </remarks>
public sealed record PluginExtensionDeployResult
{
    /// <summary>Whether the extension was deployed.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Deployed extension id on success; <c>null</c> on failure.</summary>
    public string? ExtensionId { get; init; }

    /// <summary>
    /// Files written, as forward-slash paths relative to the deployed extension directory. This is
    /// the removal manifest, recorded rather than re-derived so content added alongside a deployed
    /// extension is never collateral damage.
    /// </summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Plugin whose install failed; <c>null</c> on success.</summary>
    public string? PluginName { get; init; }

    /// <summary>Manifest field at fault; <c>null</c> on success.</summary>
    public string? Field { get; init; }

    /// <summary>Human-readable failure reason; <c>null</c> on success.</summary>
    public string? Message { get; init; }
}

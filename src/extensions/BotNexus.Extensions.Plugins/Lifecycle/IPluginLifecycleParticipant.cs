namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// Installed identity, source receipt, parsed manifest and materialised location passed to runtime
/// integration. A null manifest component remains null so convention discovery is preserved.
/// </summary>
/// <param name="Plugin">Installed identity and source/version receipt.</param>
/// <param name="Manifest">Validated manifest, including optional component declarations.</param>
/// <param name="Directory">Materialised content location, not the fetch staging directory.</param>
public sealed record PluginLifecycleContext(InstalledPlugin Plugin, PluginManifest Manifest, string Directory);

/// <summary>
/// Awaited runtime integration owned outside the core plugin model. Activation must throw on
/// refusal or incomplete startup. Deactivation must throw on incomplete teardown and be retryable.
/// Both methods may be called during compensation, with a fresh bounded cancellation token.
/// Implementations must not capture a synchronization context: the legacy synchronous removal
/// bridge waits for the same teardown contract. The manager serializes all lifecycle mutations.
/// </summary>
public interface IPluginLifecycleParticipant
{
    /// <summary>Activates validated, catalogued content before install/update can report success.</summary>
    Task ActivateAsync(PluginLifecycleContext context, CancellationToken cancellationToken);

    /// <summary>Retires runtime resources BEFORE their content or installed receipt is deleted.</summary>
    Task DeactivateAsync(PluginLifecycleContext context, CancellationToken cancellationToken);
}

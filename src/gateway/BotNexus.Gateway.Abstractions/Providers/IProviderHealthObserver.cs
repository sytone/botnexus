// IProviderHealthObserver.cs
namespace BotNexus.Gateway.Abstractions.Providers;

/// <summary>
/// Receives per-attempt provider credential outcomes so that repeated failures can be
/// turned into a health signal (#3281).
///
/// <para>
/// <b>Why an observer rather than a direct publish.</b> <c>BotNexus.Gateway.Configuration</c>
/// is a leaf project and must not reference <c>BotNexus.Gateway</c> - a fitness function
/// (<c>ConfigurationProjectBoundaryArchitectureTests</c>) fails the build if it does. The
/// credential code therefore cannot call the world event bus itself. It reports outcomes to
/// this interface, and the gateway supplies the implementation that debounces them and
/// publishes <c>health.degraded</c>. The dependency direction stays pointing inward.
/// </para>
///
/// <para>
/// <b>Implementations must not throw.</b> This is called from the credential resolution path,
/// which is on the critical path of every agent turn. An observer that throws would convert a
/// recoverable provider outage into a hard failure of the very code trying to report it. The
/// default no-op implementation exists so that a host which wants no health signalling is a
/// supported configuration rather than a null check at every call site.
/// </para>
/// </summary>
public interface IProviderHealthObserver
{
    /// <summary>
    /// Records the outcome of a single credential resolution attempt for a provider.
    /// </summary>
    /// <param name="providerId">The provider whose credential was resolved.</param>
    /// <param name="outcome">What happened, including the failure reason when applicable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordAsync(string providerId, ProviderCredentialOutcome outcome, CancellationToken cancellationToken = default);
}

/// <summary>
/// An observer that discards every outcome. Used when a host wants no provider-health
/// signalling, so that callers never have to null-check the seam.
/// </summary>
public sealed class NullProviderHealthObserver : IProviderHealthObserver
{
    /// <summary>The shared instance.</summary>
    public static readonly NullProviderHealthObserver Instance = new();

    /// <inheritdoc/>
    public Task RecordAsync(string providerId, ProviderCredentialOutcome outcome, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

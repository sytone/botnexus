namespace BotNexus.Agent.Core.ExtensionPoints.ProviderExecution;

/// <summary>
/// Invalidates host-owned provider credentials after an authentication rejection.
/// </summary>
/// <param name="provider">The rejected provider identifier.</param>
/// <param name="cancellationToken">The current provider-turn cancellation token.</param>
/// <remarks>
/// The agent layer cannot depend on the gateway credential store. Hosts that cache credentials use
/// this seam to invalidate that cache before the loop re-resolves execution options exactly once.
/// </remarks>
public delegate Task CredentialInvalidationService(string provider, CancellationToken cancellationToken);
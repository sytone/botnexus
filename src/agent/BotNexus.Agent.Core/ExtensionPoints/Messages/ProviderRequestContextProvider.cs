namespace BotNexus.Agent.Core.ExtensionPoints.Messages;

/// <summary>
/// Supplies one request-only dynamic context section after agent and provider message transformations.
/// Null or whitespace output selects the default capacity renderer. One provider owns this section;
/// compose explicitly rather than registering multicast suppliers. Exceptions and cancellation propagate
/// on normal requests; the already-decided finalizer retains its existing bounded failure behavior.
/// No output is stored, compacted, or appended to the stable system prefix. Implementations must
/// preserve truthful capacity information and return promptly while observing the supplied token.
/// </summary>
/// <param name="context">Immutable run facts without transcript mutation authority.</param>
/// <param name="cancellationToken">Cancellation of this provider request, including the finalizer deadline.</param>
/// <returns>Dynamic section text, or null/whitespace to use the core default.</returns>
public delegate Task<string?> ProviderRequestContextProvider(ProviderRequestContext context, CancellationToken cancellationToken);

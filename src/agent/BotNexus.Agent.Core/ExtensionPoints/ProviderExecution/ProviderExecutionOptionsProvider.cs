using BotNexus.Agent.Providers.Core;

namespace BotNexus.Agent.Core.ExtensionPoints.ProviderExecution;

/// <summary>
/// Resolves provider-owned execution policy for the requested provider identifier.
/// </summary>
/// <param name="provider">The provider identifier.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>Execution policy when the caller must override provider defaults; otherwise null.</returns>
/// <remarks>
/// Called before each LLM invocation. The result may carry credentials, transport policy,
/// provider retry settings, or wire timeouts. Semantic generation controls do not belong here.
/// </remarks>
public delegate Task<ProviderExecutionOptions?> ProviderExecutionOptionsProvider(string provider, CancellationToken cancellationToken);
using BotNexus.Domain.Security;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Security;

/// <summary>
/// Bridges the configuration reader to the gateway's registered credential providers.
/// </summary>
public sealed class ConfigurationSecretReferenceResolver(ISecretResolver secretResolver)
    : IConfigurationSecretReferenceResolver
{
    /// <inheritdoc />
    public async Task<string?> ResolveAsync(
        SecretRef reference,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return (await secretResolver.ResolveAsync(reference, cancellationToken).ConfigureAwait(false)).Reveal();
        }
        catch (SecretResolutionException)
        {
            return null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

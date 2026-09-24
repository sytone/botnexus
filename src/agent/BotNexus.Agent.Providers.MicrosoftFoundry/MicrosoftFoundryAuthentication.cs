using Azure.Core;
using Azure.Identity;

namespace BotNexus.Agent.Providers.MicrosoftFoundry;

/// <summary>
/// Immutable authentication configuration owned by one named Microsoft Foundry endpoint instance.
/// </summary>
public sealed record MicrosoftFoundryAuthentication
{
    private MicrosoftFoundryAuthentication(string? apiKey, TokenCredential? credential)
    {
        ApiKeyValue = apiKey;
        Credential = credential;
    }

    internal string? ApiKeyValue { get; }

    /// <summary>
    /// Gets the credential used by Entra authentication, or null for API-key authentication.
    /// </summary>
    public TokenCredential? Credential { get; }

    /// <summary>
    /// Creates authentication that sends the Foundry <c>api-key</c> header.
    /// </summary>
    public static MicrosoftFoundryAuthentication ApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return new MicrosoftFoundryAuthentication(apiKey, null);
    }

    /// <summary>
    /// Creates authentication backed by a caller-composed Azure credential.
    /// </summary>
    public static MicrosoftFoundryAuthentication Entra(TokenCredential credential) =>
        new(null, credential ?? throw new ArgumentNullException(nameof(credential)));

    /// <summary>
    /// Creates the standard developer and workload identity chain.
    /// </summary>
    public static MicrosoftFoundryAuthentication DefaultAzureCredential() =>
        Entra(new Azure.Identity.DefaultAzureCredential());

    /// <summary>
    /// Creates authentication for the host's system-assigned managed identity.
    /// </summary>
    public static MicrosoftFoundryAuthentication SystemAssignedManagedIdentity() =>
        Entra(new ManagedIdentityCredential());

    /// <summary>
    /// Creates authentication for a user-assigned managed identity selected by client id.
    /// </summary>
    public static MicrosoftFoundryAuthentication UserAssignedManagedIdentity(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return Entra(new ManagedIdentityCredential(clientId));
    }
}

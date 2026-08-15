namespace BotNexus.Extensions.GitHub;

/// <summary>
/// Platform-owned GitHub credential. The token itself is <b>not</b> part of this contract: the only
/// public operation authenticates an outbound request in place, so no caller — and in particular no
/// agent-facing tool built on top of this in a later slice of #2627 — can obtain the secret.
/// </summary>
/// <remarks>
/// Deliberate non-goals (#2732): there is no <c>GetTokenAsync</c>, no <c>Token</c> property, and no
/// method that writes the credential to the environment. Adding any of those would re-open the
/// agent-visible-credential hole this seam exists to close.
/// </remarks>
public interface IGitHubCredentialProvider
{
    /// <summary>
    /// Attaches the current installation credential to <paramref name="request"/>, minting or
    /// refreshing it transparently when the cached one is absent or expired. The caller never sees
    /// the token value.
    /// </summary>
    Task AuthenticateAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);
}

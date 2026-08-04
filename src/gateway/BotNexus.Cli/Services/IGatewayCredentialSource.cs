namespace BotNexus.Cli.Services;

/// <summary>
/// Supplies the ambient gateway credential for the *local* gateway this CLI installation
/// manages. Exists as an interface so the credential-leak contract (issue #2747) can be
/// asserted without mutating process environment variables or the user's config.json:
/// a test can hand the factory a known ambient secret and then assert, on a captured
/// outbound request, that the secret never reached an operator-supplied host.
/// </summary>
internal interface IGatewayCredentialSource
{
    /// <summary>
    /// Returns the credential for the local gateway, or <c>null</c> when none is
    /// configured. A null return is normal and expected for loopback development, where
    /// the gateway runs unauthenticated - it is not an error condition.
    /// </summary>
    string? GetGatewayCredential();
}

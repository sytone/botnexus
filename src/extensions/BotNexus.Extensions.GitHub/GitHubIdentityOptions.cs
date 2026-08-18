using System.Text.Json.Serialization;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// One named GitHub App identity profile in platform configuration (#2733).
/// </summary>
/// <remarks>
/// Every field is nullable so that an incompletely configured profile is *representable* and can
/// therefore be reported by name. Defaulting a missing app id to an empty string would push the
/// failure to GitHub, which answers with a 403 naming the identity rather than the missing setting —
/// the exact misdirection that made switching accounts look like the fix.
/// </remarks>
public sealed class GitHubIdentityOptions
{
    /// <summary>GitHub App id (the numeric app identifier used as the JWT issuer).</summary>
    [JsonPropertyName("appId")]
    public string? AppId { get; set; }

    /// <summary>Installation id whose scoped access token is minted.</summary>
    [JsonPropertyName("installationId")]
    public string? InstallationId { get; set; }

    /// <summary>Filesystem path to the GitHub App PEM private key.</summary>
    [JsonPropertyName("privateKeyPath")]
    public string? PrivateKeyPath { get; set; }
}

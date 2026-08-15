using System.Text.Json.Serialization;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// Configuration for the platform-owned GitHub App credential (#2732).
/// </summary>
/// <remarks>
/// This is deliberately <b>platform</b> configuration, not agent configuration: the private key and
/// installation identity are resolved by the gateway host, never by an agent-visible surface. The
/// minted installation token is held only inside <see cref="CachedGitHubCredentialProvider"/> and is
/// never projected into a tool result, a prompt, or an environment variable.
/// </remarks>
public sealed class GitHubCredentialOptions
{
    /// <summary>Configuration section binding these options.</summary>
    public const string SectionName = "GitHub";

    /// <summary>GitHub App id (the numeric app identifier used as the JWT issuer).</summary>
    [JsonPropertyName("appId")]
    public string? AppId { get; set; }

    /// <summary>Installation id whose scoped access token is minted.</summary>
    [JsonPropertyName("installationId")]
    public string? InstallationId { get; set; }

    /// <summary>Filesystem path to the GitHub App PEM private key.</summary>
    [JsonPropertyName("privateKeyPath")]
    public string? PrivateKeyPath { get; set; }

    /// <summary>
    /// API base address. Overridable for GitHub Enterprise Server; defaults to github.com's API.
    /// </summary>
    [JsonPropertyName("apiBaseAddress")]
    public string ApiBaseAddress { get; set; } = "https://api.github.com/";

    /// <summary>
    /// How long before the reported expiry the cached token is treated as expired. A non-zero skew
    /// stops a token that is valid at the moment of the check from expiring in flight on the wire.
    /// </summary>
    [JsonPropertyName("expirySkewSeconds")]
    public int ExpirySkewSeconds { get; set; } = 60;
}

namespace BotNexus.Agent.Providers.Copilot;

/// <summary>
/// Validates the peer-controlled <c>endpoints.api</c> host advertised by the GitHub Copilot
/// token-exchange response before it is allowed to flow onto <see cref="Core.Models.LlmModel.BaseUrl"/>
/// and carry the Copilot bearer token (#2006, defense-in-depth mirroring OpenClaw #105584).
///
/// The token-exchange JSON is controlled by whatever host answered the exchange request. Without a
/// gate, a hostile or spoofed response could advertise an attacker-chosen <c>endpoints.api</c> that
/// the runtime would then use as the model BaseUrl - carrying the bearer token to that host. This
/// allowlist restricts the advertised endpoint to https-only requests targeting a trusted host:
/// any host under <c>githubcopilot.com</c> (suffix match) plus an optional explicitly-configured
/// enterprise host.
/// </summary>
public static class CopilotEndpointAllowlist
{
    // The canonical GitHub Copilot public suffix. Both the apex (githubcopilot.com) and any
    // subdomain (api.individual.githubcopilot.com, api.enterprise.githubcopilot.com, ...) are
    // trusted. A leading '.' is used for the suffix test so lookalike domains such as
    // "evilgithubcopilot.com" or "githubcopilot.com.attacker.net" do NOT match.
    private const string GithubCopilotApexHost = "githubcopilot.com";
    private const string GithubCopilotDotSuffix = "." + GithubCopilotApexHost;

    /// <summary>
    /// Returns the advertised endpoint unchanged when it passes <see cref="IsAllowedApiEndpoint"/>,
    /// otherwise <c>null</c>. Callers use the <c>null</c> return to fall back to the default
    /// individual host rather than routing the bearer token to an unvalidated peer-advertised host.
    /// </summary>
    /// <param name="apiEndpoint">The peer-advertised <c>endpoints.api</c> value (may be null).</param>
    /// <param name="enterpriseHost">
    /// An optional explicitly-configured enterprise host (bare host or full URL) that is additionally
    /// trusted alongside the <c>*.githubcopilot.com</c> allowlist.
    /// </param>
    /// <returns>The validated endpoint, or <c>null</c> when it must be rejected.</returns>
    public static string? SanitiseApiEndpoint(string? apiEndpoint, string? enterpriseHost = null)
        => IsAllowedApiEndpoint(apiEndpoint, enterpriseHost) ? apiEndpoint : null;

    /// <summary>
    /// Returns <c>true</c> when <paramref name="apiEndpoint"/> is an absolute https URL whose host
    /// is the GitHub Copilot apex, a subdomain of it, or the explicitly-configured enterprise host.
    /// Everything else - non-https schemes, relative/malformed URIs, null/empty, and any other host -
    /// is rejected.
    /// </summary>
    /// <param name="apiEndpoint">The peer-advertised <c>endpoints.api</c> value (may be null).</param>
    /// <param name="enterpriseHost">
    /// An optional explicitly-configured enterprise host (bare host or full URL) that is additionally
    /// trusted. When supplied as a URL only its host component is compared.
    /// </param>
    public static bool IsAllowedApiEndpoint(string? apiEndpoint, string? enterpriseHost = null)
    {
        if (string.IsNullOrWhiteSpace(apiEndpoint))
            return false;

        if (!Uri.TryCreate(apiEndpoint, UriKind.Absolute, out var uri))
            return false;

        // https only - the bearer token must never travel over a non-TLS scheme.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var host = uri.Host;
        if (string.IsNullOrEmpty(host))
            return false;

        if (IsGithubCopilotHost(host))
            return true;

        var normalisedEnterprise = NormaliseHost(enterpriseHost);
        if (normalisedEnterprise is not null &&
            string.Equals(host, normalisedEnterprise, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    // Suffix match against githubcopilot.com so the apex and every subdomain are trusted while
    // lookalikes (evilgithubcopilot.com, githubcopilot.com.attacker.net) are not.
    private static bool IsGithubCopilotHost(string host)
        => string.Equals(host, GithubCopilotApexHost, StringComparison.OrdinalIgnoreCase) ||
           host.EndsWith(GithubCopilotDotSuffix, StringComparison.OrdinalIgnoreCase);

    // Accepts a bare host or a full URL and returns just the host, or null when it cannot be parsed.
    private static string? NormaliseHost(string? hostOrUrl)
    {
        if (string.IsNullOrWhiteSpace(hostOrUrl))
            return null;

        if (Uri.TryCreate(hostOrUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return uri.Host;

        return hostOrUrl.Trim();
    }
}

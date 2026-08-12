namespace BotNexus.Extensions.Mcp;

/// <summary>
/// The single scheme-validation seam for MCP server URLs (issue #3012).
/// <para>
/// <b>Why this exists:</b> <see cref="McpServerConfig.Url"/> is a free-form string taken from
/// per-agent extension JSON, while <see cref="McpToolContributor"/> injects a resolved BotNexus
/// provider API key into that transport as <c>Authorization: Bearer &lt;token&gt;</c>. Without a
/// shared validation seam the two sites make no assertion about each other, so a single
/// <c>http://</c> typo transmits a live provider credential in cleartext. Both the transport
/// factory and the token-injection site consume this helper so the rule has exactly one
/// definition and cannot drift.
/// </para>
/// <para>
/// <b>The rule:</b> a URL that will carry credentials must use <see cref="Uri.UriSchemeHttps"/>
/// unless it is a loopback address. The loopback carve-out is a deliberate developer affordance
/// (local MCP servers rarely have certificates) and matches the "HTTPS except loopback"
/// constraint OpenClaw adopted for its OAuth callback origin.
/// </para>
/// </summary>
public static class McpUrlSecurity
{
    /// <summary>
    /// Validates an MCP server URL, applying the TLS requirement only when the request will
    /// carry credentials. Non-credentialed plaintext endpoints (for example an unauthenticated
    /// MCP server on a trusted LAN) remain supported, because tightening those would be a
    /// availability change rather than a credential-disclosure fix.
    /// </summary>
    /// <param name="url">The raw configured URL string.</param>
    /// <param name="carriesCredentials">
    /// <c>true</c> when an <c>Authorization</c> header (or an <c>auth</c> provider reference that
    /// resolves to one) will be sent to this endpoint.
    /// </param>
    /// <param name="endpoint">The parsed endpoint when validation succeeds; otherwise <c>null</c>.</param>
    /// <param name="error">A human-readable reason when validation fails; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when the URL may be used; <c>false</c> when the server must be skipped.</returns>
    public static bool TryValidate(
        string? url,
        bool carriesCredentials,
        out Uri? endpoint,
        out string? error)
    {
        endpoint = null;
        error = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "url is empty.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            error = "url is not an absolute URI.";
            return false;
        }

        var isHttp = string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal);
        var isHttps = string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);

        if (!isHttp && !isHttps)
        {
            error = $"url scheme '{parsed.Scheme}' is not supported; expected http or https.";
            return false;
        }

        if (carriesCredentials && !isHttps && !parsed.IsLoopback)
        {
            error =
                $"url scheme '{parsed.Scheme}' would transmit credentials in cleartext to non-loopback host " +
                $"'{parsed.Host}'; https is required.";
            return false;
        }

        endpoint = parsed;
        return true;
    }

    /// <summary>
    /// Returns <c>true</c> when the supplied headers contain an <c>Authorization</c> entry, i.e.
    /// the request built from them will carry a credential.
    /// </summary>
    public static bool HeadersCarryCredentials(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
            return false;

        foreach (var key in headers.Keys)
        {
            if (string.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

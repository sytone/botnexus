using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Api;

/// <summary>
/// Resolves the URL the gateway startup banner announces (issue #2929).
/// </summary>
/// <remarks>
/// <para>
/// This was an inline null-coalescing chain in <c>Program.cs</c> whose final fallback was a stale
/// <c>http://localhost:5000</c> literal, so a fresh install with no configured listen URL was told the
/// gateway was on a port nothing was listening on. Extracting it does two things the inline form could
/// not: it gives the precedence order a single definition, and it makes the banner assertable without
/// booting a host - AC4 asks for a named test that reddens when the banner and the real bind diverge,
/// and an expression buried in a 90-line logging method cannot have one.
/// </para>
/// <para>
/// The precedence is deliberate and ordered by how close each source is to what Kestrel actually did:
/// the addresses the host really bound win outright, then the operator's configured value, then the
/// ambient <c>ASPNETCORE_URLS</c>, and only then the shared default. Any earlier source that is present
/// but blank is skipped rather than announced - a banner reading "Gateway starting on " is worse than
/// one reading the default.
/// </para>
/// </remarks>
public static class GatewayStartupUrlResolver
{
    /// <summary>
    /// Returns the URL to announce, given the addresses the host bound, the configured listen URL and
    /// the raw <c>ASPNETCORE_URLS</c> value.
    /// </summary>
    /// <param name="boundAddresses">
    /// Addresses the server actually bound (<c>app.Urls</c>). When non-empty this always wins: it is
    /// the only source that reports what happened rather than what was requested.
    /// </param>
    /// <param name="configuredListenUrl">The operator's <c>gateway.listenUrl</c>, if any.</param>
    /// <param name="aspNetCoreUrls">The raw semicolon-separated <c>ASPNETCORE_URLS</c> value, if any.</param>
    public static string Resolve(
        IEnumerable<string>? boundAddresses,
        string? configuredListenUrl,
        string? aspNetCoreUrls)
    {
        var bound = boundAddresses?.FirstOrDefault(static url => !string.IsNullOrWhiteSpace(url));
        if (!string.IsNullOrWhiteSpace(bound))
        {
            return bound;
        }

        if (!string.IsNullOrWhiteSpace(configuredListenUrl))
        {
            return configuredListenUrl;
        }

        var fromEnvironment = aspNetCoreUrls?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static url => !string.IsNullOrWhiteSpace(url));

        return string.IsNullOrWhiteSpace(fromEnvironment)
            ? GatewayDefaults.LoopbackListenUrl
            : fromEnvironment;
    }
}

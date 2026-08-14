using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Commands;

/// <summary>
/// The single definition of what a gateway listen URL binds to, plus the two canonical listen URLs
/// <c>botnexus init</c> can emit.
/// <para>
/// Issue #2798 deliberately places this in one place. The change has two halves that must agree
/// exactly: the <c>init</c> default (which must NOT be a wildcard) and the <c>doctor config</c>
/// finding (which must fire on precisely the addresses <c>init</c> refuses to emit). Two independent
/// spellings of "is this a wildcard bind" drift the moment someone adds a form - <c>+</c>, <c>[::]</c>,
/// a bracketed IPv6 literal - to only one of them, and the failure mode is silent: init keeps
/// producing a safe default while doctor stops reporting an operator's real exposure, or doctor
/// starts warning about a bind init never generates. Both call sites compose from
/// <see cref="IsWildcard(string?)"/> so a new wildcard form is recognised everywhere the instant it
/// is added here.
/// </para>
/// </summary>
public static class GatewayBindAddress
{
    /// <summary>
    /// The loopback listen URL a fresh install receives (issue #2798). Binding only the loopback
    /// interface keeps the portal, SignalR hub, agent REST API and admin endpoints off the local
    /// network until an operator explicitly opts in.
    /// </summary>
    public const string LoopbackListenUrl = GatewayDefaults.LoopbackListenUrl;

    /// <summary>
    /// The all-interfaces listen URL an operator opts into for remote/mesh (NetBird, Tailscale,
    /// reverse proxy) access. Byte-identical to the value <c>init</c> generated before #2798, so an
    /// operator who wants the previous behaviour gets exactly the previous field value.
    /// </summary>
    public const string WildcardListenUrl = GatewayDefaults.WildcardListenUrl;

    /// <summary>
    /// The surface a wildcard bind exposes to every reachable network. Named explicitly so the
    /// doctor finding tells an operator what is reachable, not merely that a wildcard is set
    /// (issue #2798 AC4).
    /// </summary>
    public const string ExposedSurfaceDescription =
        "the portal UI, the SignalR hub, the agent REST API and the gateway admin endpoints";

    /// <summary>
    /// Reads <c>gateway.listenUrl</c> from a config document, or null when absent. Reads the
    /// persisted value by canonical path rather than a bound configuration object, so a doctor
    /// finding reports what is actually on disk rather than a defaulted value.
    /// </summary>
    public static string? ReadListenUrl(ConfigDocument config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.TryGetString(ListenUrlPath, out var url) ? url : null;
    }

    /// <summary>The canonical path of the gateway listen URL.</summary>
    internal const string ListenUrlPath = "gateway.listenUrl";

    /// <summary>
    /// Returns true when the listen URL binds every network interface rather than one specific
    /// address. Recognises the IPv4 any-address (<c>0.0.0.0</c>), the IPv6 any-address (<c>::</c>,
    /// bracketed or not), and the Kestrel/HTTP.sys wildcard hosts <c>*</c> and <c>+</c>. A null,
    /// blank or hostless value is not treated as a wildcard: an unreadable listen URL is a different
    /// problem and must not be reported as an exposure the operator never created.
    /// </summary>
    public static bool IsWildcard(string? listenUrl)
    {
        var host = ExtractHost(listenUrl);
        return host is "0.0.0.0" or "*" or "+" or "::" or "[::]" or "::0" or "[::0]";
    }

    /// <summary>
    /// Pulls the host portion out of a listen URL without using <see cref="Uri"/>, which rejects the
    /// <c>*</c> and <c>+</c> wildcard hosts Kestrel accepts. Returns null when no host is present.
    /// </summary>
    private static string? ExtractHost(string? listenUrl)
    {
        if (string.IsNullOrWhiteSpace(listenUrl))
            return null;

        var remainder = listenUrl.Trim();

        var schemeIndex = remainder.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
            remainder = remainder[(schemeIndex + 3)..];

        // Trim any path/query so "http://0.0.0.0:5005/admin" still resolves to the host.
        var pathIndex = remainder.IndexOfAny(['/', '?', '#']);
        if (pathIndex >= 0)
            remainder = remainder[..pathIndex];

        // A bare IPv6 any-address has no port to strip and would be mangled by the colon split
        // below, so recognise it before splitting.
        if (remainder is "::" or "::0")
            return remainder;

        // A bracketed IPv6 literal keeps its brackets; the port (if any) follows the closing one.
        if (remainder.StartsWith('['))
        {
            var close = remainder.IndexOf(']');
            return close < 0 ? remainder : remainder[..(close + 1)];
        }

        var portIndex = remainder.LastIndexOf(':');
        if (portIndex >= 0)
            remainder = remainder[..portIndex];

        return remainder.Length == 0 ? null : remainder;
    }
}

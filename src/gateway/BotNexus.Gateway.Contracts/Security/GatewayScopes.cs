namespace BotNexus.Gateway.Abstractions.Security;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The single declaration of the gateway's per-caller permission vocabulary, and the only place a
/// request is mapped onto the scope it requires (#2621).
/// <para>
/// <b>Why one place.</b> <see cref="GatewayCallerIdentity.Permissions"/> was populated by every
/// authentication path and demanded by config validation, yet nothing ever read it to make an
/// authorization decision - so a narrowly-scoped key reached every endpoint an operator key did.
/// The fix needs a vocabulary, and a vocabulary spelled as free-form strings compared at N call
/// sites drifts: this repo already needed a dedicated PR to collapse five duplicated
/// <c>HashActor</c> copies. Every scope name and every route-to-scope mapping therefore lives
/// here, and <c>GatewayScopeCoverageFenceArchitectureTests</c> fails the build if a new
/// authenticated controller route has no entry.
/// </para>
/// <para>
/// <b>Shape of the vocabulary.</b> A scope is <c>&lt;resource&gt;:&lt;access&gt;</c>, where
/// resource is the first path segment beneath <c>/api</c> and access is <c>read</c> for safe
/// methods (GET/HEAD/OPTIONS) or <c>write</c> for everything else. That keeps the vocabulary
/// mechanically derivable from the routing table instead of being a second, hand-curated list that
/// can silently disagree with it.
/// </para>
/// </summary>
public static class GatewayScopes
{
    /// <summary>The wildcard scope. A caller holding it is authorized for every scope.</summary>
    public const string Wildcard = "*";

    /// <summary>Read access suffix, applied to safe HTTP methods.</summary>
    public const string ReadAccess = "read";

    /// <summary>Write access suffix, applied to every non-safe HTTP method.</summary>
    public const string WriteAccess = "write";

    /// <summary>Scope a satellite needs to open its gateway connection.</summary>
    public const string SatelliteConnect = "satellite:connect";

    /// <summary>Scope a satellite needs to report liveness.</summary>
    public const string SatelliteHeartbeat = "satellite:heartbeat";

    /// <summary>
    /// Every resource the authenticated REST surface exposes, i.e. the first path segment under
    /// <c>/api</c> for each controller route. A route whose resource is absent here resolves to no
    /// scope, which is denied - see <see cref="Resolve"/>. Adding an endpoint therefore requires
    /// adding its resource, which is the point.
    /// </summary>
    public static readonly IReadOnlyList<string> Resources =
    [
        "agents",
        "channels",
        "chat",
        "commands",
        "config",
        "conversations",
        "cron",
        "diagnostics",
        "exchanges",
        "extensions",
        "federation",
        "gateway",
        "locations",
        "log",
        "logs",
        "memory",
        "models",
        "nav-order",
        "providers",
        "satellites",
        "sessions",
        "stats",
        "subagents",
        "tools",
        "webhooks"
    ];

    /// <summary>
    /// The closed set of every scope string the platform recognises: the wildcard, the two
    /// satellite scopes, and <c>read</c>/<c>write</c> for each resource. A permission on an
    /// identity that is not in this set is an <b>unknown</b> scope and is refused rather than
    /// ignored (constraint 3 - fail closed, not open).
    /// </summary>
    public static readonly IReadOnlySet<string> All = BuildAll();

    /// <summary>
    /// Resolves the scope a request requires, or <see langword="null"/> when the request maps onto
    /// no known resource. A null result is <b>not</b> "no scope needed" - callers must treat it as
    /// a denial, because an unmapped authenticated path is exactly the silent bypass this exists
    /// to prevent.
    /// </summary>
    /// <param name="path">Request path, e.g. <c>/api/agents/foo</c>.</param>
    /// <param name="method">HTTP method, or <c>WS</c> for a WebSocket upgrade.</param>
    /// <returns>The required scope, or <see langword="null"/> if the path maps to no resource.</returns>
    public static string? Resolve(string? path, string? method)
    {
        // The SignalR hub is how a satellite (and the portal) establishes its live connection. It
        // is not an /api resource route, so it must be mapped explicitly or the narrowed satellite
        // identity would lose the one thing it exists to do the moment enforcement is enabled.
        if (!string.IsNullOrWhiteSpace(path) &&
            path.StartsWith("/hub", StringComparison.OrdinalIgnoreCase))
        {
            return SatelliteConnect;
        }

        var resource = ExtractResource(path);
        if (resource is null)
            return null;

        return $"{resource}:{AccessFor(method)}";
    }

    /// <summary>
    /// Returns whether <paramref name="permissions"/> authorizes <paramref name="requiredScope"/>.
    /// <para>
    /// Deliberately strict on both edges: a null <paramref name="requiredScope"/> (unmapped path)
    /// is refused, and a permission string outside <see cref="All"/> grants nothing at all - an
    /// unknown scope must never be read as a wildcard or quietly skipped.
    /// </para>
    /// </summary>
    /// <param name="permissions">The caller's granted permissions.</param>
    /// <param name="requiredScope">The scope the request requires, from <see cref="Resolve"/>.</param>
    /// <returns><see langword="true"/> when the caller is authorized.</returns>
    public static bool IsAuthorized(IReadOnlyList<string>? permissions, string? requiredScope)
    {
        if (string.IsNullOrWhiteSpace(requiredScope))
            return false;

        if (permissions is null || permissions.Count == 0)
            return false;

        foreach (var permission in permissions)
        {
            if (string.IsNullOrWhiteSpace(permission))
                continue;

            // An unrecognised scope confers nothing. Skipping it here (rather than comparing it)
            // is what makes a typo'd or retired permission fail closed.
            if (!All.Contains(permission))
                continue;

            if (string.Equals(permission, Wildcard, StringComparison.Ordinal))
                return true;

            if (string.Equals(permission, requiredScope, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the resource segment a path maps onto, or <see langword="null"/> when the path is
    /// not an <c>/api</c> route with a known resource. Used by the fitness fence as well as by
    /// <see cref="Resolve"/> so both agree by construction.
    /// </summary>
    /// <param name="path">Request path.</param>
    /// <returns>The resource segment, or <see langword="null"/>.</returns>
    public static string? ExtractResource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return null;

        if (!string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase))
            return null;

        var resource = segments[1].ToLowerInvariant();
        return Resources.Contains(resource) ? resource : null;
    }

    private static string AccessFor(string? method)
        => IsSafeMethod(method) ? ReadAccess : WriteAccess;

    private static bool IsSafeMethod(string? method)
        => string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlySet<string> BuildAll()
    {
        var all = new HashSet<string>(StringComparer.Ordinal)
        {
            Wildcard,
            SatelliteConnect,
            SatelliteHeartbeat
        };

        foreach (var resource in Resources)
        {
            all.Add($"{resource}:{ReadAccess}");
            all.Add($"{resource}:{WriteAccess}");
        }

        return all;
    }
}

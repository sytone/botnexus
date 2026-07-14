using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// The capability a hub method requires from the calling connection. Control/mutation
/// methods require <see cref="Control"/>; passive inspection methods require <see cref="Read"/>.
/// A connection granted <see cref="Control"/> implicitly satisfies <see cref="Read"/>.
/// </summary>
public enum HubScope
{
    /// <summary>Passive read/inspect access (e.g. list agents, read status).</summary>
    Read,

    /// <summary>Write/control access (e.g. send, steer, abort, compact, reset).</summary>
    Control
}

/// <summary>
/// Per-method least-privilege scope guard for <see cref="GatewayHub"/> control methods.
/// Mirrors OpenClaw's <c>isApprovalMethod</c> guard (openclaw#93656): a connection scoped
/// to read/notify only must not be able to invoke a write-capable control method.
/// </summary>
/// <remarks>
/// <para>
/// The hub-level <see cref="SignalRAuthPolicy"/> gate answers "is this caller authenticated?".
/// This guard answers the finer-grained "is this caller allowed to invoke <em>this</em>
/// method?" by comparing the method's required <see cref="HubScope"/> against the scopes
/// carried on the caller's <see cref="ClaimsPrincipal"/>.
/// </para>
/// <para>
/// Scopes are read from the OAuth-style <c>scope</c> claim (space-delimited) and/or one or
/// more <c>scp</c> claims. The recognised vocabulary is <see cref="ReadScopeValue"/> and
/// <see cref="ControlScopeValue"/>; granting control implicitly grants read.
/// </para>
/// <para>
/// <b>Backward compatibility:</b> when the caller presents <em>no</em> recognised scope claim
/// at all (the common case today, where connections are authenticated but not yet scope-tagged,
/// or auth is disabled entirely), the guard permits every method. Least-privilege enforcement
/// only engages once a connection actually carries scope claims -- so a deliberately
/// read-only-scoped connection is restricted, while existing full-trust clients keep working.
/// </para>
/// </remarks>
public static class HubScopeGuard
{
    /// <summary>The scope value that grants passive read/inspect access.</summary>
    public const string ReadScopeValue = "gateway:read";

    /// <summary>The scope value that grants write/control access (implies read).</summary>
    public const string ControlScopeValue = "gateway:control";

    // Recognised OAuth scope claim types. "scope" carries a single space-delimited value;
    // "scp" (and the AAD long-form URI) may appear as one-value-per-claim.
    private static readonly string[] ScopeClaimTypes =
    [
        "scope",
        "scp",
        "http://schemas.microsoft.com/identity/claims/scope"
    ];

    /// <summary>
    /// Throws a <see cref="HubException"/> when the caller's scopes do not satisfy the
    /// <paramref name="required"/> scope for <paramref name="methodName"/>. No-op when the
    /// caller presents no recognised scope claim (backward-compatible full-trust default).
    /// </summary>
    /// <param name="user">The calling connection's principal (from <c>Context.User</c>).</param>
    /// <param name="required">The scope the invoked method requires.</param>
    /// <param name="methodName">The hub method name, surfaced in the rejection message.</param>
    public static void EnsureScope(ClaimsPrincipal? user, HubScope required, string methodName)
    {
        var granted = ExtractScopes(user);

        // No scope claims presented at all -> full-trust legacy caller; permit everything.
        if (granted.Count == 0)
            return;

        if (IsSatisfied(granted, required))
            return;

        throw new HubException(
            $"Connection is not authorized to invoke '{methodName}'; a {DescribeRequired(required)} scope is required.");
    }

    /// <summary>
    /// Returns <see langword="true"/> when the caller's <paramref name="granted"/> scopes
    /// satisfy the <paramref name="required"/> scope. Exposed for testing the pure decision.
    /// </summary>
    internal static bool IsSatisfied(IReadOnlyCollection<string> granted, HubScope required)
        => required switch
        {
            // Control requires the control scope explicitly.
            HubScope.Control => granted.Contains(ControlScopeValue),
            // Read is satisfied by either read or the stronger control scope.
            HubScope.Read => granted.Contains(ReadScopeValue) || granted.Contains(ControlScopeValue),
            _ => false
        };

    /// <summary>
    /// Extracts the recognised gateway scopes from the principal's scope claims, splitting the
    /// space-delimited <c>scope</c> claim and collecting individual <c>scp</c> claims. Returns
    /// an empty set when the principal is null or carries no recognised scope value.
    /// </summary>
    internal static IReadOnlyCollection<string> ExtractScopes(ClaimsPrincipal? user)
    {
        if (user is null)
            return [];

        var scopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in user.Claims)
        {
            if (!ScopeClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase))
                continue;

            foreach (var token in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token is ReadScopeValue or ControlScopeValue)
                    scopes.Add(token);
            }
        }

        return scopes;
    }

    private static string DescribeRequired(HubScope required)
        => required switch
        {
            HubScope.Control => $"'{ControlScopeValue}'",
            HubScope.Read => $"'{ReadScopeValue}' or '{ControlScopeValue}'",
            _ => "valid"
        };
}

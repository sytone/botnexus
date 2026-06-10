using Microsoft.AspNetCore.SignalR;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// Resolves the stable user identity from authenticated claims for SignalR connections.
/// Reads the <c>oid</c> claim (Entra ID / Azure AD object ID) first, falling back to the
/// standard <c>sub</c> claim for generic OIDC providers. This ensures
/// <see cref="HubCallerContext.UserIdentifier"/> carries a stable, authentication-derived
/// identity rather than the ephemeral <see cref="HubCallerContext.ConnectionId"/>.
/// </summary>
public sealed class ClaimsUserIdProvider : IUserIdProvider
{
    /// <summary>The Entra ID object identifier claim type.</summary>
    public const string OidClaimType = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    /// <summary>The short-form <c>oid</c> claim emitted by some token configurations.</summary>
    public const string OidShortClaimType = "oid";

    /// <summary>The standard OIDC subject claim type.</summary>
    public const string SubClaimType = "sub";

    /// <inheritdoc/>
    public string? GetUserId(HubConnectionContext connection)
    {
        var user = connection.User;
        if (user?.Identity?.IsAuthenticated != true)
            return null;

        // Prefer Entra `oid` (object ID) — stable across token refreshes and app registrations.
        var oid = user.FindFirst(OidClaimType)?.Value
                ?? user.FindFirst(OidShortClaimType)?.Value;
        if (!string.IsNullOrWhiteSpace(oid))
            return oid;

        // Fall back to standard OIDC `sub` claim.
        var sub = user.FindFirst(SubClaimType)?.Value;
        return string.IsNullOrWhiteSpace(sub) ? null : sub;
    }
}

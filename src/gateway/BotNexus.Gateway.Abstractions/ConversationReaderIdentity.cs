using System.Security.Claims;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions;

/// <summary>
/// Resolves the server-owned identity used for per-reader conversation state. Authenticated
/// readers use stable identity-provider claims; an unauthenticated single-user host uses one
/// explicit local owner rather than a device or connection identifier.
/// </summary>
public static class ConversationReaderIdentity
{
    /// <summary>The stable reader shared by every client of an unauthenticated local host.</summary>
    public static ConversationReaderId LocalOwner { get; } = ConversationReaderId.From("local-owner");

    /// <summary>The Entra ID object identifier claim type.</summary>
    public const string OidClaimType = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    /// <summary>The short-form <c>oid</c> claim emitted by some token configurations.</summary>
    public const string OidShortClaimType = "oid";

    /// <summary>The standard OIDC subject claim type.</summary>
    public const string SubClaimType = "sub";

    /// <summary>
    /// Resolves an authenticated Entra <c>oid</c> (long form, then short form), then OIDC
    /// <c>sub</c>. An unauthenticated principal maps to <see cref="LocalOwner"/>. An authenticated
    /// principal without a stable claim fails closed rather than using a display name or client
    /// supplied value.
    /// </summary>
    public static ConversationReaderId? Resolve(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
            return LocalOwner;

        var value = FirstNonBlankClaim(principal, OidClaimType, OidShortClaimType, SubClaimType);
        return value is null ? null : ConversationReaderId.From(value);
    }

    private static string? FirstNonBlankClaim(ClaimsPrincipal principal, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = principal.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }
}

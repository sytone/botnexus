using BotNexus.Gateway.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// Resolves the server-owned conversation reader identity for SignalR connections. Authenticated
/// readers use stable <c>oid</c>/<c>sub</c> claims; unauthenticated single-user hosts use the shared
/// local-owner identity rather than the ephemeral <see cref="HubCallerContext.ConnectionId"/>.
/// </summary>
public sealed class ClaimsUserIdProvider : IUserIdProvider
{
    /// <summary>The Entra ID object identifier claim type.</summary>
    public const string OidClaimType = ConversationReaderIdentity.OidClaimType;

    /// <summary>The short-form <c>oid</c> claim emitted by some token configurations.</summary>
    public const string OidShortClaimType = ConversationReaderIdentity.OidShortClaimType;

    /// <summary>The standard OIDC subject claim type.</summary>
    public const string SubClaimType = ConversationReaderIdentity.SubClaimType;

    /// <inheritdoc/>
    public string? GetUserId(HubConnectionContext connection)
        => ConversationReaderIdentity.Resolve(connection.User)?.Value;
}

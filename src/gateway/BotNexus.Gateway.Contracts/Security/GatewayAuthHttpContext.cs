namespace BotNexus.Gateway.Abstractions.Security;

/// <summary>
/// Shared HTTP-context contract populated by gateway authentication middleware and consumed by
/// extension-hosted endpoints after the request has been authenticated.
/// </summary>
public static class GatewayAuthHttpContext
{
    /// <summary>The <c>HttpContext.Items</c> key containing the authenticated caller identity.</summary>
    public const string CallerIdentityItemKey = "BotNexus.Gateway.CallerIdentity";
}

using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// Registers the SignalR channel's host-level services that the extension loader's
/// contract-based auto-discovery cannot express: the <see cref="SignalRAuthPolicy.PolicyName"/>
/// authorization policy required by <see cref="GatewayHub"/>'s <c>[Authorize]</c> attribute, and
/// the claims-based <see cref="IUserIdProvider"/> that overrides SignalR's connection-id default.
/// </summary>
/// <remarks>
/// Without this contributor the <c>SignalRHubAuth</c> policy is never registered in a production
/// host, causing <c>AuthorizationMiddleware</c> to throw "policy not found" and the hub negotiate
/// request to fail with HTTP 500. The same registrations are applied in tests via
/// <c>AddSignalRChannelForTests</c>.
/// </remarks>
public sealed class SignalRServiceContributor : IServiceContributor
{
    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSignalRAuthPolicy();

        // Override SignalR's DefaultUserIdProvider (registered via TryAdd by AddSignalR) so the
        // hub's UserIdentifier is derived from authenticated claims rather than the connection id.
        services.AddSingleton<IUserIdProvider, ClaimsUserIdProvider>();
    }
}

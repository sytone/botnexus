using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// Defines the authorization policy for the SignalR hub. When authentication schemes
/// are registered (e.g. JWT Bearer), the policy requires authenticated users. When no
/// schemes are configured, the policy is satisfied by any caller (including anonymous)
/// for backward compatibility during the Phase 1 transition.
/// </summary>
public static class SignalRAuthPolicy
{
    /// <summary>The policy name applied to <see cref="GatewayHub"/>.</summary>
    public const string PolicyName = "SignalRHubAuth";

    /// <summary>
    /// Registers the <see cref="PolicyName"/> authorization policy. Call from
    /// <c>AddSignalRChannel</c> DI setup.
    /// </summary>
    public static IServiceCollection AddSignalRAuthPolicy(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.AddPolicy(PolicyName, policy =>
            {
                // The custom requirement is evaluated by SignalRAuthRequirementHandler,
                // which checks at runtime whether any authentication scheme is registered.
                policy.Requirements.Add(new SignalRAuthRequirement());
            });
        });

        services.AddSingleton<IAuthorizationHandler, SignalRAuthRequirementHandler>();
        return services;
    }
}

/// <summary>
/// Custom authorization requirement that the <see cref="SignalRAuthRequirementHandler"/>
/// evaluates at runtime based on whether authentication schemes are configured.
/// </summary>
public sealed class SignalRAuthRequirement : IAuthorizationRequirement;

/// <summary>
/// Evaluates the <see cref="SignalRAuthRequirement"/>:
/// <list type="bullet">
/// <item>If authentication schemes are registered → requires authenticated user</item>
/// <item>If no schemes are registered → always succeeds (backward compat)</item>
/// </list>
/// </summary>
public sealed class SignalRAuthRequirementHandler : AuthorizationHandler<SignalRAuthRequirement>
{
    private readonly IAuthenticationSchemeProvider _schemeProvider;

    /// <summary>
    /// Creates a new handler instance.
    /// </summary>
    public SignalRAuthRequirementHandler(IAuthenticationSchemeProvider schemeProvider)
    {
        _schemeProvider = schemeProvider;
    }

    /// <inheritdoc/>
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SignalRAuthRequirement requirement)
    {
        var schemes = await _schemeProvider.GetAllSchemesAsync();
        var hasAuthSchemes = schemes.Any();

        if (!hasAuthSchemes)
        {
            // No authentication configured — permit anonymous for backward compat
            context.Succeed(requirement);
            return;
        }

        // Authentication is configured — require an authenticated identity
        if (context.User.Identity?.IsAuthenticated == true)
        {
            context.Succeed(requirement);
        }
        // Otherwise: requirement not satisfied → 401/403
    }
}

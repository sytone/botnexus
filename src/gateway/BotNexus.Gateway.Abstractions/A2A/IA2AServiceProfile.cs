namespace BotNexus.Gateway.Abstractions.A2A;

/// <summary>Applies profile-owned authentication after the generic client has validated a destination.</summary>
/// <param name="request">The validated outbound A2A request.</param>
/// <param name="cancellationToken">Cancels authentication with the bounded delegation.</param>
public delegate ValueTask A2AServiceRequestAuthenticator(
    HttpRequestMessage request,
    CancellationToken cancellationToken);

/// <summary>Supplies the approved connection policy for one ready provider-neutral A2A profile.</summary>
/// <param name="ApprovedOrigin">The only origin the generic A2A client may discover and invoke.</param>
/// <param name="AuthenticateDiscoveryAsync">Optional profile-owned discovery authentication.</param>
/// <param name="AuthenticateSubmissionAsync">Optional profile-owned task-submission authentication.</param>
/// <param name="AdditionalBlockedHosts">Optional operator-blocked hosts layered onto the shared SSRF policy.</param>
public sealed record A2AServiceConnection(
    Uri ApprovedOrigin,
    A2AServiceRequestAuthenticator? AuthenticateDiscoveryAsync = null,
    A2AServiceRequestAuthenticator? AuthenticateSubmissionAsync = null,
    IReadOnlyList<string>? AdditionalBlockedHosts = null);

/// <summary>Describes the caller's consequential-action boundary for one remote delegation.</summary>
/// <param name="Objective">The bounded objective presented to the remote agent.</param>
/// <param name="AllowedActions">The explicit actions the caller permits for this delegation.</param>
/// <param name="ApprovalBoundary">The authority or human-approval boundary the remote agent must not cross.</param>
public sealed record A2ADelegationAuthorizationRequest(
    string Objective,
    IReadOnlyList<string> AllowedActions,
    string ApprovalBoundary);

/// <summary>Returns a profile-owned policy decision without exposing credentials or provider-specific types.</summary>
/// <param name="IsAllowed">Whether the delegation may proceed to discovery and submission.</param>
/// <param name="Reason">A bounded safe reason when the profile denies the delegation.</param>
public sealed record A2ADelegationAuthorization(bool IsAllowed, string? Reason)
{
    /// <summary>Allows the bounded delegation.</summary>
    public static A2ADelegationAuthorization Allow() => new(true, null);

    /// <summary>Denies the bounded delegation with a caller-safe reason.</summary>
    public static A2ADelegationAuthorization Deny(string reason)
        => new(false, string.IsNullOrWhiteSpace(reason) ? "The A2A service profile denied this delegation." : reason.Trim());
}

/// <summary>
/// Describes one provider-neutral A2A service profile registered by an extension through ordinary host DI.
/// Implementations own endpoint, authentication, and readiness policy; callers never expose those values as tool arguments.
/// </summary>
public interface IA2AServiceProfile
{
    /// <summary>Gets the stable, configuration-facing profile identifier.</summary>
    string Id { get; }

    /// <summary>Gets the operator-facing profile name.</summary>
    string DisplayName { get; }

    /// <summary>Gets whether this profile currently has enough policy and authentication configuration to accept calls.</summary>
    bool IsReady { get; }

    /// <summary>Applies profile-owned remote-agent and action policy before any discovery or authentication occurs.</summary>
    ValueTask<A2ADelegationAuthorization> AuthorizeAsync(
        A2ADelegationAuthorizationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves approved connection policy without returning raw credentials.</summary>
    ValueTask<A2AServiceConnection> GetConnectionAsync(CancellationToken cancellationToken = default);
}

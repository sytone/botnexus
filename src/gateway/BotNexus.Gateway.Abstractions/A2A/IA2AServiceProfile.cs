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

    /// <summary>Resolves approved connection policy without returning raw credentials.</summary>
    ValueTask<A2AServiceConnection> GetConnectionAsync(CancellationToken cancellationToken = default);
}

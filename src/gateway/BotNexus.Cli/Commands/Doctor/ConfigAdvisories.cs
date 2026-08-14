using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Commands.Doctor;

/// <summary>
/// A read-only <c>doctor config</c> finding: something an operator should see and decide about, but
/// which the tool must never silently rewrite.
/// <para>
/// Issue #2798 forced this distinction. <see cref="IConfigCheck"/> exists to close gaps in a config
/// the platform can safely fill in for you, and <c>doctor config --yes</c> applies every applicable
/// one. A wildcard <c>listenUrl</c> is categorically different: it may be a deliberate operator
/// choice enabling remote/mesh access, so "fixing" it would break a working deployment and would
/// violate #2798 AC3, which requires an existing wildcard config to be left unchanged by any command
/// that is not explicitly setting the value. An advisory therefore has no <c>Apply</c> at all - the
/// type system, not reviewer diligence, is what prevents a future contributor from wiring it into
/// the auto-apply loop.
/// </para>
/// </summary>
public interface IConfigAdvisory
{
    /// <summary>Stable identifier used in output and machine-readable reporting.</summary>
    string Id { get; }

    /// <summary>Returns true when this advisory applies to the given config document.</summary>
    bool IsApplicable(ConfigDocument config);

    /// <summary>
    /// The finding text shown to the operator. Must name the concrete exposure or risk, not merely
    /// state that a setting has a particular value.
    /// </summary>
    string Describe(ConfigDocument config);

    /// <summary>What the operator can do about it, if they want to.</summary>
    string Remediation { get; }
}

/// <summary>
/// Reports that <c>gateway.listenUrl</c> binds a wildcard address, so every reachable network can
/// talk to the gateway (issue #2798 AC4).
/// <para>
/// Advisory, never auto-applied: a wildcard bind is a legitimate configuration for remote or mesh
/// access, and #2798 AC3 requires an existing config to survive untouched. The point is that the
/// exposure becomes visible rather than inherited silently - which is exactly how it reached every
/// fresh install before #2798 changed the generated default. The wildcard predicate itself lives in
/// <see cref="GatewayBindAddress"/> so this check and the <c>init</c> default can never disagree
/// about what counts as a wildcard.
/// </para>
/// </summary>
public sealed class WildcardListenUrlAdvisory : IConfigAdvisory
{
    /// <inheritdoc />
    public string Id => "gateway-wildcard-bind";

    /// <inheritdoc />
    public bool IsApplicable(ConfigDocument config)
        => GatewayBindAddress.IsWildcard(GatewayBindAddress.ReadListenUrl(config));

    /// <inheritdoc />
    public string Describe(ConfigDocument config)
        => $"gateway.listenUrl is '{GatewayBindAddress.ReadListenUrl(config)}' - a wildcard bind. "
           + $"Every network this host can reach can talk to {GatewayBindAddress.ExposedSurfaceDescription}.";

    /// <inheritdoc />
    public string Remediation =>
        $"If this host is only used locally, set gateway.listenUrl to \"{GatewayBindAddress.LoopbackListenUrl}\". "
        + "If remote or mesh access is intended, keep the wildcard bind and put an authenticated "
        + "reverse proxy or a private overlay network in front of it. Not changed automatically - "
        + "a wildcard bind can be deliberate.";
}

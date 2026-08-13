namespace BotNexus.Gateway.Configuration;

/// <summary>
/// The single definition of the gateway's default listen port and loopback listen URL (issue #2929).
/// </summary>
/// <remarks>
/// <para>
/// The default moved to 5005 and was centralised in <c>GatewayBindAddress</c> / <c>GatewayClientFactory</c>,
/// but those live in <c>BotNexus.Cli</c>, which the gateway host cannot reference - the dependency runs the
/// other way. So the startup banner in <c>Program.cs</c> kept its own pre-centralisation literal and still
/// announced <c>http://localhost:5000</c>. Because it is a *fallback*, it is silent whenever a listen URL is
/// configured, which it normally is; it only surfaces on a fresh install, which is precisely the audience
/// least able to tell a wrong banner from a broken gateway (same defect class as #2858).
/// </para>
/// <para>
/// This type lives in <c>BotNexus.Gateway.Configuration</c> because that is the deepest leaf every consumer
/// already references: the CLI, the gateway host and the auth handler can all see it, so the constant can be
/// the one definition rather than a fourth copy. The CLI constants remain as named aliases of these values -
/// callers keep their existing, well-scoped names while there is exactly one literal in the tree.
/// </para>
/// <para>
/// Deliberately NOT the container default: the shipped <c>Dockerfile</c> binds <c>http://+:5000</c> inside the
/// container and publishes it as <c>5000</c>. That is an explicit <c>ASPNETCORE_URLS</c> value, not a fallback,
/// so it never reaches this constant.
/// </para>
/// </remarks>
public static class GatewayDefaults
{
    /// <summary>The gateway's default TCP listen port.</summary>
    public const int ListenPort = 5005;

    /// <summary>
    /// The loopback listen URL a fresh install receives, and the fallback every gateway-facing default
    /// resolves to when no URL is configured.
    /// </summary>
    public const string LoopbackListenUrl = "http://localhost:5005";

    /// <summary>
    /// The all-interfaces listen URL an operator opts into for remote/mesh access.
    /// </summary>
    public const string WildcardListenUrl = "http://0.0.0.0:5005";
}

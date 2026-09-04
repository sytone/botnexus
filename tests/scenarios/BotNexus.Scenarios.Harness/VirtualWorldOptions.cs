namespace BotNexus.Scenarios.Harness;

/// <summary>
/// Configuration for a <see cref="VirtualWorld"/> instance. Mirrors the subset of gateway
/// behaviour that scenarios typically need to vary without leaking DI primitives onto the
/// public surface (which the <c>ScenarioHarness_PublicSurface_DoesNotLeakDiPrimitives</c>
/// architecture rule enforces).
/// </summary>
public sealed class VirtualWorldOptions
{
    /// <summary>
    /// Capability flags for the single virtual channel auto-registered with the world.
    /// Override to construct capability-gating scenarios (e.g. <c>SupportsSteering=false</c>).
    /// </summary>
    public VirtualChannelAdapterOptions ChannelOptions { get; init; } = new();

    /// <summary>
    /// Optional response factory for the in-memory <see cref="ScenarioFakeApiProvider"/>. When
    /// null, the provider emits the literal string "ok" for every turn — sufficient for most
    /// routing / lifecycle scenarios that don't assert on reply content.
    /// </summary>
    public Func<int, BotNexus.Agent.Providers.Core.Models.Context, string>? ResponseFactory { get; init; }

    /// <summary>
    /// Default system prompt baked into agents created via <see cref="VirtualWorld.GivenAgentAsync"/>
    /// when the caller does not pass one explicitly.
    /// </summary>
    public string DefaultSystemPrompt { get; init; } = "You are a helpful scenario test agent.";

    /// <summary>
    /// Minimum log level emitted by the in-process gateway. Default is <see cref="Microsoft.Extensions.Logging.LogLevel.Warning"/>
    /// — bump to <see cref="Microsoft.Extensions.Logging.LogLevel.Debug"/> when diagnosing why a scenario doesn't reach an expected
    /// outbound and the failure message alone isn't enough.
    /// </summary>
    public Microsoft.Extensions.Logging.LogLevel LogLevel { get; init; } = Microsoft.Extensions.Logging.LogLevel.Warning;

    /// <summary>
    /// Maximum wait used by <see cref="VirtualWorld.WaitForOutboundAsync"/> when the caller
    /// does not pass an explicit timeout. Default 5s — chosen high enough to absorb agent
    /// startup + provider dispatch + outbound capture on a slow CI box but low enough that
    /// a regression failing this gate produces fast, readable failures.
    /// </summary>
    public TimeSpan DefaultOutboundWaitTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>
    /// Boots the world the way a real install boots (#3699): a <c>config.json</c> on disk, the
    /// platform configuration pipeline bound over it, the bundled-agent reconciler, and the
    /// config-driven agent source - instead of the default programmatic registration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Default worlds deliberately register agents in code and strip every hosted service but
    /// <c>GatewayHost</c>, which makes them fast but blind to the entire config plane. A bundled
    /// agent is only real if it survives that plane: the reconciler writes JSON, the config source
    /// turns JSON into a descriptor, the validator accepts it, and the registry ends up holding a
    /// live agent. Opting in here is what lets a scenario assert that chain end to end rather than
    /// asserting that a template was inserted into a file.
    /// </para>
    /// <para>
    /// The seed agent named by <see cref="SeedConfigAgentId"/> is written into that config so the
    /// reconciler has a provider/model pair to copy - mirroring an installation that already has
    /// one working agent, which is the case the Trailguide insert is designed for.
    /// </para>
    /// </remarks>
    public bool ReconcileBundledAgents { get; init; }
    /// <summary>
    /// Id of the pre-existing enabled agent written into the seeded <c>config.json</c> when
    /// <see cref="ReconcileBundledAgents"/> is set. It supplies the provider/model the reconciler
    /// copies onto the bundled entry.
    /// </summary>
    public string SeedConfigAgentId { get; init; } = "seed-agent";
}

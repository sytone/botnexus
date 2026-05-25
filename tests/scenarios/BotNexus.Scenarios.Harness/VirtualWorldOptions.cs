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
}

namespace BotNexus.Scenarios.Harness;

/// <summary>
/// Configuration for a <see cref="VirtualChannelAdapter"/> instance. Defaults match a
/// modern interactive channel (streaming on, steering on, follow-up on) so the common path
/// "just works" without ceremony; capability-gating scenarios opt into a narrower set.
/// </summary>
public sealed record VirtualChannelAdapterOptions
{
    /// <summary>
    /// Optional adapter instance identifier. Required when multiple virtual adapters of the
    /// same channel type live in one scenario (e.g. proving multi-channel fan-out).
    /// Returns <c>null</c> when only one virtual adapter is registered.
    /// </summary>
    public string? AdapterId { get; init; }

    /// <summary>Human-readable display name for the adapter. Defaults to "Virtual".</summary>
    public string? DisplayName { get; init; }

    /// <summary>Whether the adapter advertises streaming-delta support to the router.</summary>
    public bool SupportsStreaming { get; init; } = true;

    /// <summary>Whether the adapter advertises mid-response steering support to the router.</summary>
    public bool SupportsSteering { get; init; } = true;

    /// <summary>Whether the adapter advertises follow-up message controls to the router.</summary>
    public bool SupportsFollowUp { get; init; } = true;

    /// <summary>Whether the adapter advertises thinking/progress rendering to the router.</summary>
    public bool SupportsThinkingDisplay { get; init; }

    /// <summary>Whether the adapter advertises tool-call activity rendering to the router.</summary>
    public bool SupportsToolDisplay { get; init; }

    /// <summary>Whether the adapter advertises inbound image attachment support to the router.</summary>
    public bool SupportsInboundImages { get; init; }
}

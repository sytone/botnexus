namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Bounds and deadlines for the per-agent inbound webhook queue (#3851).
/// Bound from <c>gateway:webhooks:inboundQueue</c>.
/// </summary>
/// <remarks>
/// These are admission-control bounds, not tuning preferences. Before #3851 the webhook path had
/// neither: concurrency was resolved implicitly by blocking on the per-session write lock, which
/// gives mutual exclusion but no bound, no deadline, and no way to see a backlog forming.
/// </remarks>
public sealed class WebhookInboundQueueOptions
{
    /// <summary>Default number of deliveries that may wait for one busy agent.</summary>
    public const int DefaultMaxQueueDepth = 16;

    /// <summary>Default ceiling on a single background webhook agent turn.</summary>
    public static readonly TimeSpan DefaultRunTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How many deliveries may WAIT for one agent's execution slot before further callers are
    /// refused outright. The in-flight delivery is excluded, so an uncontended delivery never
    /// consumes depth. Values below 1 are treated as 1.
    /// </summary>
    public int MaxQueueDepth { get; set; } = DefaultMaxQueueDepth;

    /// <summary>
    /// Wall-clock ceiling applied to a background (async/callback mode) webhook run, covering both
    /// the queued wait and the agent turn. Values at or below zero are treated as the default:
    /// "no timeout at all" is the defect this option exists to close, so it must not be reachable
    /// through misconfiguration.
    /// </summary>
    public TimeSpan RunTimeout { get; set; } = DefaultRunTimeout;

    /// <summary>The effective bound, guaranteed to be at least 1.</summary>
    public int EffectiveMaxQueueDepth => Math.Max(1, MaxQueueDepth);

    /// <summary>The effective run ceiling, guaranteed to be positive.</summary>
    public TimeSpan EffectiveRunTimeout => RunTimeout > TimeSpan.Zero ? RunTimeout : DefaultRunTimeout;
}

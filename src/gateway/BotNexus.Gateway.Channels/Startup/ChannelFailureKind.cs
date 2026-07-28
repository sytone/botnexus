namespace BotNexus.Gateway.Channels.Startup;

/// <summary>
/// Classification of a channel failure, used to decide whether a retry can plausibly succeed.
/// </summary>
/// <remarks>
/// This is deliberately a small, shared vocabulary rather than a per-call-site judgement.
/// The startup path (#2447) retries <see cref="Transient"/> failures with a bounded backoff and
/// gives up immediately on <see cref="Terminal"/> ones; the steady-state polling loops (#2386)
/// need exactly the same distinction to bound their currently-unbounded retries, and are expected
/// to consume <see cref="ChannelFailureClassifier"/> rather than grow a second, divergent copy.
/// </remarks>
public enum ChannelFailureKind
{
    /// <summary>
    /// A momentary fault - upstream 5xx, gateway timeout, socket reset, DNS/IO blip.
    /// Retrying after a backoff may succeed.
    /// </summary>
    Transient,

    /// <summary>
    /// A deterministic fault - invalid or revoked credentials, malformed configuration, a
    /// permanently rejected request. Retrying re-issues the same failure, so the caller must
    /// fail once, log clearly, and stop.
    /// </summary>
    Terminal,
}

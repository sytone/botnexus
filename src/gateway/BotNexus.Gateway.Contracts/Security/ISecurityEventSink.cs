namespace BotNexus.Gateway.Abstractions.Security;

/// <summary>
/// A trusted-only sink for <see cref="SecurityEvent"/> records emitted at the gateway's
/// security enforcement boundaries.
/// </summary>
/// <remarks>
/// <para>
/// Step 1/5 of the security-event taxonomy (#1532, part of #1526). Implementations record
/// security events to a <strong>trusted</strong> store -- these events are deliberately kept
/// OFF the public activity/diagnostic stream so that an untrusted consumer cannot read who
/// was denied, which secrets were referenced, or how authorization decisions were made.
/// A future trusted-only diagnostics surface (Step 5) reads back recent events via
/// <see cref="Snapshot"/>.
/// </para>
/// <para>
/// Implementations must be safe to call concurrently from any enforcement-path thread.
/// Recording must never throw for benign back-pressure -- a full sink evicts rather than blocks.
/// </para>
/// </remarks>
public interface ISecurityEventSink
{
    /// <summary>
    /// Records a security event. Thread-safe; must not block on back-pressure
    /// (a bounded implementation evicts the oldest event instead).
    /// </summary>
    /// <param name="securityEvent">The event to record. Must not be null.</param>
    void Record(SecurityEvent securityEvent);

    /// <summary>
    /// Returns a point-in-time snapshot of the retained events, most-recent first.
    /// The returned list is a copy and is safe to enumerate without further locking.
    /// </summary>
    IReadOnlyList<SecurityEvent> Snapshot();

    /// <summary>The number of events currently retained.</summary>
    int Count { get; }

    /// <summary>Removes all retained events.</summary>
    void Clear();
}

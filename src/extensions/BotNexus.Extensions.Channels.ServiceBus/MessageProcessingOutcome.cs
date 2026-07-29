namespace BotNexus.Extensions.Channels.ServiceBus;

/// <summary>
/// Result of a single Service Bus message processing attempt (#2525).
/// Distinguishes a failure of the <em>work</em> from a failure of the <em>acknowledgement</em>,
/// so redelivery caused by a lapsed lock is not reported as a processing error.
/// </summary>
internal enum MessageProcessingOutcome
{
    /// <summary>The handler succeeded and the message was completed.</summary>
    Completed,

    /// <summary>The handler threw; the message was abandoned for retry.</summary>
    AbandonedAfterHandlerFailure,

    /// <summary>Shutdown was requested mid-handler; the message was abandoned for retry.</summary>
    AbandonedForShutdown,

    /// <summary>
    /// The handler succeeded but the lock had expired, so completion failed. No abandon is
    /// attempted because the lock is already invalid; the broker will redeliver.
    /// </summary>
    CompleteFailedLockLost,

    /// <summary>
    /// The handler succeeded but completion failed for a non-lock reason. The message was
    /// abandoned deliberately to release the still-held lock promptly.
    /// </summary>
    CompleteFailedAbandoned,
}

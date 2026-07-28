namespace BotNexus.Gateway.Abstractions.Events;

/// <summary>
/// The gateway-side seam for emitting channel-neutral conversation facts to every registered
/// <see cref="IConversationEventSink"/> (issue #2085).
/// <para>
/// Callers are hot paths - the agent token callback in particular - so publication is
/// deliberately a hand-off, not a delivery. Implementations accept the event, guarantee
/// per-conversation ordering, and fan out asynchronously; a slow or hung extension must never
/// stall the agent loop that produced the token.
/// </para>
/// </summary>
public interface IConversationEventPublisher
{
    /// <summary>
    /// Hands a conversation fact to the publication pump.
    /// </summary>
    /// <param name="conversationEvent">The immutable fact to publish.</param>
    /// <param name="cancellationToken">Cancels the hand-off itself, not downstream sink delivery.</param>
    /// <returns>
    /// <c>true</c> when the event was accepted for delivery; <c>false</c> when it was shed
    /// because the conversation's bounded buffer was full or the publisher is shutting down.
    /// Callers on hot paths are expected to ignore the result; diagnostics consume it.
    /// </returns>
    ValueTask<bool> PublishAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits until everything accepted before this call has been offered to every sink.
    /// <para>
    /// Exists so that tests and orderly shutdown can observe a settled system instead of
    /// racing the pump. Production hot paths must not call it.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait; already-queued events keep draining.</param>
    Task WaitForDrainAsync(CancellationToken cancellationToken = default);
}

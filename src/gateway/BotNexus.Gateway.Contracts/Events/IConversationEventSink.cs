namespace BotNexus.Gateway.Abstractions.Events;

/// <summary>
/// Implemented by a channel extension that wants to observe channel-neutral conversation
/// facts (issue #2085).
/// <para>
/// The publisher offers every event to every registered sink. Deciding whether the event is
/// relevant - whether this extension actually holds a connected recipient for the
/// conversation - is the sink's job, not the publisher's. A sink with nothing to do must
/// return successfully without side effects; that is the normal case, not an error.
/// </para>
/// </summary>
/// <remarks>
/// Contract implementers must guarantee:
/// <list type="bullet">
/// <item><description>The event is treated as shared immutable state; it must never be mutated.</description></item>
/// <item><description>Returning without doing anything is a valid, non-exceptional outcome.</description></item>
/// <item><description>Throwing is isolated by the publisher and never suppresses delivery to other
/// sinks, but a sink that throws routinely will still be logged as faulty.</description></item>
/// <item><description>The supplied cancellation token is honoured. The publisher bounds how long it
/// waits for a sink, and a sink that ignores cancellation is simply abandoned mid-flight - it
/// must not leave the extension in a broken state if that happens.</description></item>
/// </list>
/// </remarks>
public interface IConversationEventSink
{
    /// <summary>
    /// Offers one conversation fact to this extension.
    /// </summary>
    /// <param name="conversationEvent">The immutable fact being published.</param>
    /// <param name="cancellationToken">
    /// Cancelled when the publisher's per-sink budget expires or the gateway is shutting
    /// down. Sinks must observe it rather than blocking the publication pump.
    /// </param>
    Task OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken = default);
}

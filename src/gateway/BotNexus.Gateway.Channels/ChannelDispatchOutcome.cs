namespace BotNexus.Gateway.Channels;

/// <summary>
/// Explicit result of an inbound dispatch attempt made through
/// <see cref="ChannelAdapterBase.DispatchInboundAsync"/> (#3594).
/// </summary>
/// <remarks>
/// Before #3594 this seam returned <see cref="System.Threading.Tasks.Task"/>, so "I forwarded this
/// message" and "I silently dropped it" were the same observable outcome. A durable channel that
/// settles on the handler returning without throwing therefore acknowledged a message it had never
/// dispatched, and the broker never redelivered it. The outcome is now stated rather than inferred.
/// </remarks>
public enum ChannelDispatchOutcome
{
    /// <summary>The message reached the Gateway routing pipeline.</summary>
    Dispatched,

    /// <summary>
    /// The sender failed the configured allow-list. This is a deliberate policy drop, not a
    /// delivery failure: durable channels must still settle the message, because redelivering it
    /// would only be blocked again.
    /// </summary>
    BlockedByAllowList,

    /// <summary>
    /// No dispatcher was registered - the adapter was stopped or stopping - so the message was
    /// discarded without being routed. Durable channels must NOT settle on this outcome; the
    /// message has to be abandoned so the broker redelivers it after restart.
    /// </summary>
    AdapterStopped,
}

namespace BotNexus.Gateway.Channels;

/// <summary>
/// Thrown by a durable channel adapter when an inbound message could not be dispatched because the
/// adapter is stopped or stopping (#3594).
/// </summary>
/// <remarks>
/// The point of this type is settlement: a broker-backed adapter derives acknowledgement from
/// whether its handler threw. Returning normally on a dropped message acknowledges work that never
/// happened. Throwing this instead routes the message onto the abandon path, so it is redelivered
/// after the gateway restarts and the at-least-once contract documented on
/// <c>ServiceBusChannelAdapter.ProcessMessageCoreAsync</c> holds through shutdown.
/// </remarks>
public sealed class ChannelStoppedException : Exception
{
    /// <summary>Creates the exception for a named channel type.</summary>
    /// <param name="channelType">The channel that dropped the message.</param>
    public ChannelStoppedException(string channelType)
        : base($"Channel '{channelType}' is stopped; the inbound message was not dispatched.")
        => ChannelType = channelType;

    /// <summary>The channel that dropped the message.</summary>
    public string ChannelType { get; }
}

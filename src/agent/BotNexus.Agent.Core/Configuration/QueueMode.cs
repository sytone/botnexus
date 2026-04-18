namespace BotNexus.Agent.Core.Configuration;

/// <summary>
/// Controls whether message queues are consumed in full or one item at a time.
/// </summary>
/// <remarks>
/// Used by steering and follow-up queues to control how many messages are drained per loop iteration.
/// </remarks>
public enum QueueMode
{
    /// <summary>
    /// Consume one queued message per loop iteration.
    /// </summary>
    /// <remarks>
    /// Spreads messages across turns. Useful for rate-limiting or progressive injection.
    /// </remarks>
    OneAtATime,

    /// <summary>
    /// Consume all available queued messages in a single drain operation.
    /// </summary>
    All,
}

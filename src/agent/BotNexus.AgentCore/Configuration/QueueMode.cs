namespace BotNexus.AgentCore.Configuration;

/// <summary>
/// Controls whether message queues are consumed in full or one item at a time.
/// </summary>
public enum QueueMode
{
    /// <summary>
    /// Consume all available queued messages.
    /// </summary>
    All,

    /// <summary>
    /// Consume one queued message per loop iteration.
    /// </summary>
    OneAtATime,
}

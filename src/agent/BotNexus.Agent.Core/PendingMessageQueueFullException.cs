namespace BotNexus.Agent.Core;

/// <summary>
/// Thrown when a message is offered to a bounded pending-message queue that is already
/// at capacity.
/// </summary>
/// <remarks>
/// Introduced with the bounded follow-up queue (#2438). The queue deliberately rejects
/// rather than drops: a follow-up that cannot be accepted must surface to the sender as
/// a refusal. Callers must not catch this and continue silently - that would reintroduce
/// exactly the message loss the bound is meant to make visible.
/// </remarks>
public sealed class PendingMessageQueueFullException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PendingMessageQueueFullException"/> class.
    /// </summary>
    /// <param name="capacity">The queue capacity that was reached.</param>
    public PendingMessageQueueFullException(int capacity)
        : base($"The pending message queue is full ({capacity} undrained messages). The message was not accepted.")
        => Capacity = capacity;

    /// <summary>Gets the capacity that was reached when the message was rejected.</summary>
    public int Capacity { get; }
}

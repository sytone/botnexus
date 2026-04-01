using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;

namespace BotNexus.Core.Bus;

/// <summary>Extension methods for <see cref="IMessageBus"/>.</summary>
public static class MessageBusExtensions
{
    /// <summary>Publishes a message synchronously (fire-and-forget).</summary>
    public static void Publish(this IMessageBus bus, InboundMessage message)
        => bus.PublishAsync(message).AsTask().GetAwaiter().GetResult();
}

using BotNexus.Core.Models;

namespace BotNexus.Core.Abstractions;

/// <summary>Contract for an async message bus.</summary>
public interface IMessageBus
{
    /// <summary>Publishes an inbound message to the bus.</summary>
    ValueTask PublishAsync(InboundMessage message, CancellationToken cancellationToken = default);

    /// <summary>Reads the next available message, waiting asynchronously if none is ready.</summary>
    ValueTask<InboundMessage> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns an async stream of inbound messages.</summary>
    IAsyncEnumerable<InboundMessage> ReadAllAsync(CancellationToken cancellationToken = default);
}

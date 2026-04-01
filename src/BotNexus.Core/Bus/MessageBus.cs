using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;

namespace BotNexus.Core.Bus;

/// <summary>
/// In-process async message bus backed by <see cref="System.Threading.Channels.Channel{T}"/>.
/// Analogous to Python's asyncio.Queue.
/// </summary>
public sealed class MessageBus : IMessageBus
{
    private readonly Channel<InboundMessage> _channel;

    public MessageBus(int capacity = 1000)
    {
        _channel = Channel.CreateBounded<InboundMessage>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    /// <inheritdoc/>
    public ValueTask PublishAsync(InboundMessage message, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(message, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<InboundMessage> ReadAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAsync(cancellationToken);

    /// <inheritdoc/>
    public async IAsyncEnumerable<InboundMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return message;
    }

    /// <summary>Completes the writer, signalling no more messages will be published.</summary>
    public void Complete() => _channel.Writer.Complete();
}

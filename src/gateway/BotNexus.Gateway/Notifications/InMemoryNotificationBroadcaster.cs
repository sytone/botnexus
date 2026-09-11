using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BotNexus.Gateway.Abstractions.Notifications;

namespace BotNexus.Gateway.Notifications;

/// <summary>
/// In-memory notification fan-out over bounded channels, mirroring
/// <c>InMemoryActivityBroadcaster</c>.
/// </summary>
/// <remarks>
/// Drop-oldest on a full subscriber channel. For activity that loses an event outright; here it
/// costs nothing durable, because the notification is already in the store before it is broadcast -
/// a client that missed the push still sees it on its next read.
/// </remarks>
public sealed class InMemoryNotificationBroadcaster : INotificationBroadcaster
{
    private readonly List<Channel<Notification>> _subscribers = [];
    private readonly Lock _sync = new();

    /// <inheritdoc />
    public ValueTask PublishAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        if (notification is null)
            return ValueTask.CompletedTask;

        lock (_sync)
        {
            foreach (var channel in _subscribers)
                channel.Writer.TryWrite(notification);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Notification> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Smaller than the activity buffer on purpose: notifications are rare and durable, so a
        // deep queue would only delay a client discovering it had fallen behind.
        var channel = Channel.CreateBounded<Notification>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        lock (_sync)
            _subscribers.Add(channel);

        try
        {
            await foreach (var notification in channel.Reader.ReadAllAsync(cancellationToken))
                yield return notification;
        }
        finally
        {
            lock (_sync)
                _subscribers.Remove(channel);

            channel.Writer.TryComplete();
        }
    }
}

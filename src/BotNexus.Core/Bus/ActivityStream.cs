using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;

namespace BotNexus.Core.Bus;

/// <summary>
/// In-process activity stream that broadcasts events to all active subscribers.
/// Each subscriber receives its own copy of every event via independent channels.
/// </summary>
public sealed class ActivityStream : IActivityStream
{
    private readonly ConcurrentDictionary<string, Channel<ActivityEvent>> _subscribers = new();

    /// <inheritdoc/>
    public ValueTask PublishAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default)
    {
        foreach (var (_, channel) in _subscribers)
        {
            // Best-effort write — if a subscriber is slow, we drop rather than block
            channel.Writer.TryWrite(activityEvent);
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public IActivitySubscription Subscribe()
    {
        var id = Guid.NewGuid().ToString("N");
        var channel = Channel.CreateBounded<ActivityEvent>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _subscribers[id] = channel;
        return new Subscription(id, channel.Reader, this);
    }

    private void Unsubscribe(string id)
    {
        if (_subscribers.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }

    private sealed class Subscription : IActivitySubscription
    {
        private readonly string _id;
        private readonly ChannelReader<ActivityEvent> _reader;
        private readonly ActivityStream _owner;
        private bool _disposed;

        public Subscription(string id, ChannelReader<ActivityEvent> reader, ActivityStream owner)
        {
            _id = id;
            _reader = reader;
            _owner = owner;
        }

        public ValueTask<ActivityEvent> ReadAsync(CancellationToken cancellationToken = default)
            => _reader.ReadAsync(cancellationToken);

        public async IAsyncEnumerable<ActivityEvent> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var evt in _reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return evt;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Unsubscribe(_id);
        }
    }
}

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Activity;

/// <summary>
/// In-memory activity broadcaster using <see cref="Channel{T}"/> fan-out.
/// Each subscriber gets a bounded channel with drop-oldest semantics.
/// </summary>
public sealed class InMemoryActivityBroadcaster : IActivityBroadcaster
{
    private readonly List<Channel<GatewayActivity>> _subscribers = [];
    private readonly Lock _sync = new();
    private readonly ILogger<InMemoryActivityBroadcaster> _logger;

    public InMemoryActivityBroadcaster(ILogger<InMemoryActivityBroadcaster> logger) => _logger = logger;

    /// <inheritdoc />
    public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            foreach (var channel in _subscribers)
                channel.Writer.TryWrite(activity); // Drop if subscriber is slow
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<GatewayActivity> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<GatewayActivity>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        lock (_sync) _subscribers.Add(channel);

        try
        {
            await foreach (var activity in channel.Reader.ReadAllAsync(cancellationToken))
                yield return activity;
        }
        finally
        {
            lock (_sync) _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }
}

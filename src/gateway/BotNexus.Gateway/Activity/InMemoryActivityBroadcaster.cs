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
    /// <remarks>
    /// <para>
    /// <b>Registration happens here, not on first enumeration.</b> This method is deliberately not
    /// an <c>async</c> iterator. An iterator's body does not run until the first
    /// <c>MoveNextAsync()</c>, so a subscriber that had called <c>SubscribeAsync</c> was not
    /// actually subscribed yet, and every activity published in that window went to nobody -
    /// silently, because publishing to zero subscribers is indistinguishable from publishing to a
    /// slow one under drop-oldest.
    /// </para>
    /// <para>
    /// The window is small but real, and it is exactly the shape callers hit: subscribe, then do
    /// the thing you want to observe. It surfaced as an intermittently failing test whose reported
    /// error - <c>NotSupportedException: Specified method is not supported.</c> - named neither the
    /// race nor the missed event, because disposing an async iterator while its
    /// <c>MoveNextAsync</c> is still pending throws that instead. Creating and registering the
    /// channel before returning closes the window: once this method has returned, the caller is
    /// subscribed, whether or not it has begun enumerating.
    /// </para>
    /// <para>
    /// The cost is that a caller who calls this and never enumerates leaks its channel into
    /// <see cref="_subscribers"/>, where the old shape would have cleaned up in the iterator's
    /// <c>finally</c>. That is bounded rather than unbounded - the channel is capped at 500 with
    /// drop-oldest, so it cannot grow - and it only happens on misuse, whereas the missed-event
    /// window happened on correct use.
    /// </para>
    /// </remarks>
    public IAsyncEnumerable<GatewayActivity> SubscribeAsync(CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<GatewayActivity>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        lock (_sync) _subscribers.Add(channel);

        return DrainAsync(channel, cancellationToken);
    }

    /// <summary>
    /// Yields one already-registered subscriber's activities, and deregisters it on the way out.
    /// </summary>
    private async IAsyncEnumerable<GatewayActivity> DrainAsync(
        Channel<GatewayActivity> channel,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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

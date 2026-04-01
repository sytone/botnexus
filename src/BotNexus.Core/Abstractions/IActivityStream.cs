using BotNexus.Core.Models;

namespace BotNexus.Core.Abstractions;

/// <summary>Contract for a system-wide activity stream that broadcasts events to all subscribers.</summary>
public interface IActivityStream
{
    /// <summary>Publishes an activity event to all subscribers.</summary>
    ValueTask PublishAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default);

    /// <summary>Subscribes to activity events. Returns a subscription that yields events.</summary>
    IActivitySubscription Subscribe();
}

/// <summary>A subscription to the activity stream. Dispose to unsubscribe.</summary>
public interface IActivitySubscription : IDisposable
{
    /// <summary>Reads the next available activity event.</summary>
    ValueTask<ActivityEvent> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns an async stream of activity events.</summary>
    IAsyncEnumerable<ActivityEvent> ReadAllAsync(CancellationToken cancellationToken = default);
}

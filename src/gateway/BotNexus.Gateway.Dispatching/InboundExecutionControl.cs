namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Optional execution-origin contract for an inbound message whose processing lifetime is owned by
/// the producer rather than by the transport connection.
/// </summary>
public sealed class InboundExecutionControl
{
    private readonly Func<Task> _onStartedAsync;
    private int _started;

    /// <summary>Creates a producer-owned execution contract.</summary>
    /// <param name="cancellationToken">
    /// Cancels processor work after the queue worker has selected this message. This is distinct from
    /// ordinary caller cancellation, which remains detached from processing.
    /// </param>
    /// <param name="onStartedAsync">
    /// Persists or publishes the actual execution-start transition.
    /// </param>
    public InboundExecutionControl(CancellationToken cancellationToken, Func<Task> onStartedAsync)
    {
        ArgumentNullException.ThrowIfNull(onStartedAsync);
        CancellationToken = cancellationToken;
        _onStartedAsync = onStartedAsync;
    }

    /// <summary>The producer-owned execution cancellation token.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Publishes actual start exactly once. A deadline that expires before this method is reached
    /// prevents a terminal queued run from later regressing to running.
    /// </summary>
    public async Task NotifyStartedAsync()
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _onStartedAsync().ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _started, 0);
            throw;
        }
    }
}

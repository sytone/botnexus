using System.Threading.Channels;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// Core streaming primitive for LLM responses.
/// Providers push events in; consumers iterate asynchronously.
/// Uses System.Threading.Channels as the C# equivalent of pi-mono's queue+waiting pattern.
/// </summary>
public sealed class LlmStream : IAsyncEnumerable<AssistantMessageEvent>
{
    private readonly Channel<AssistantMessageEvent> _channel =
        Channel.CreateUnbounded<AssistantMessageEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });

    private readonly TaskCompletionSource<AssistantMessage> _resultTcs = new();
    private bool _done;

    /// <summary>
    /// Push an event into the stream. Providers call this to emit events.
    /// When a DoneEvent or ErrorEvent is pushed, the final result is captured.
    /// </summary>
    public void Push(AssistantMessageEvent evt)
    {
        if (_done)
            return;

        switch (evt)
        {
            case DoneEvent done:
                _resultTcs.TrySetResult(done.Message);
                _done = true;
                break;
            case ErrorEvent error:
                _resultTcs.TrySetResult(error.Error);
                _done = true;
                break;
        }

        _channel.Writer.TryWrite(evt);

        if (_done)
            _channel.Writer.TryComplete();
    }

    /// <summary>
    /// Signal the stream is complete with a final result.
    /// </summary>
    /// <param name="result">The turn's final message. Required.</param>
    /// <remarks>
    /// The result is mandatory by design (#3293). It was previously optional and defaulted to
    /// <c>null</c>, which made <c>End()</c> and <c>End(message)</c> look equally legitimate at a
    /// call site even though only the latter left the stream usable: the <c>null</c> path completed
    /// the event channel but never completed the result task, so <see cref="GetResultAsync"/> hung
    /// forever with no error and no cancellation. Producers that genuinely have no message to report
    /// must now say so explicitly via <see cref="EndWithoutResult(string)"/>, which fails the
    /// awaiter loudly instead of stranding it.
    /// </remarks>
    public void End(AssistantMessage result)
    {
        ArgumentNullException.ThrowIfNull(result);

        _done = true;
        _resultTcs.TrySetResult(result);

        _channel.Writer.TryComplete();
    }

    /// <summary>
    /// Signal the stream is complete with no final result, faulting any awaiter of
    /// <see cref="GetResultAsync"/> with a <see cref="LlmStreamIncompleteException"/>.
    /// </summary>
    /// <param name="reason">Why no result was produced; surfaced in the exception message.</param>
    /// <remarks>
    /// This is the explicit, representable form of the state that <c>End(null)</c> used to express
    /// silently (#3293). If a terminal event already supplied a result, that result stands - the
    /// underlying <see cref="TaskCompletionSource{TResult}"/> is only transitioned when it is still
    /// pending, so a late abort cannot retroactively fail a turn that already succeeded.
    /// </remarks>
    public void EndWithoutResult(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        _done = true;
        _resultTcs.TrySetException(new LlmStreamIncompleteException(reason));

        _channel.Writer.TryComplete();
    }

    /// <summary>
    /// Cancellation-aware form of <see cref="EndWithoutResult(string)"/>: ends the stream as
    /// <em>cancelled</em> when <paramref name="cancellationToken"/> is signalled, and as an
    /// incomplete <em>fault</em> otherwise.
    /// </summary>
    /// <param name="reason">Why no result was produced; used only on the fault path.</param>
    /// <param name="cancellationToken">The turn's token. Its state - not the exception type that
    /// prompted the call - decides which path is taken.</param>
    /// <remarks>
    /// This is the single shared seam for the #3382 distinction, so no provider has to restate it.
    /// A cancelled turn is normal control flow: the consumer has already unwound and nothing awaits
    /// <see cref="GetResultAsync"/>, so faulting the result task left an
    /// <see cref="LlmStreamIncompleteException"/> permanently unobserved. The finalizer thread then
    /// raised it as a <c>TaskScheduler.UnobservedTaskException</c> and the last-chance handler wrote
    /// a fatal-looking breadcrumb for a gateway that was serving normally.
    /// <para>
    /// Cancelling the result task rather than faulting it is what actually closes the hole: a task in
    /// the <see cref="TaskStatus.Canceled"/> state is never reported as an unobserved exception,
    /// whereas a faulted one is.
    /// </para>
    /// <para>
    /// The guard keys off <b>token state, not exception type</b>. An
    /// <see cref="OperationCanceledException"/> raised with no cancellation requested is a genuine
    /// fault - a socket abort, or a library misreporting a failure - and keeps the incomplete-result
    /// path unchanged, so this can never degenerate into a blanket swallow.
    /// </para>
    /// </remarks>
    public void EndWithoutResult(string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (cancellationToken.IsCancellationRequested)
        {
            EndCancelled(cancellationToken);
            return;
        }

        EndWithoutResult(reason);
    }

    /// <summary>
    /// Signal the stream was cancelled: completes the event channel and transitions the result task
    /// to <see cref="TaskStatus.Canceled"/> rather than faulted, so an unobserved result cannot
    /// escape to the finalizer thread as an unobserved exception (#3382).
    /// </summary>
    /// <param name="cancellationToken">The token that was signalled; recorded on the cancelled task.</param>
    /// <remarks>
    /// Uses <c>TrySetCanceled</c> for the same reason <see cref="EndWithoutResult(string)"/> uses
    /// <c>TrySetException</c>: a result already captured from a terminal event wins, so a late
    /// cancellation cannot retroactively cancel a turn that already produced a message.
    /// </remarks>
    public void EndCancelled(CancellationToken cancellationToken)
    {
        _done = true;
        _resultTcs.TrySetCanceled(cancellationToken);

        _channel.Writer.TryComplete();
    }

    /// <summary>
    /// Terminate the stream as FAULTED: the event channel is completed with
    /// <paramref name="exception"/> so a consumer enumerating the stream observes the throw, and the
    /// result task is faulted with the same exception (#3567).
    /// </summary>
    /// <param name="exception">The failure that terminated the turn.</param>
    /// <remarks>
    /// <para>
    /// This is the seam that lets a provider-layer failure reach the agent loop's retry lane at all.
    /// <c>AgentLoopRunner.ExecuteWithRetryAsync</c> retries only inside a <c>catch</c>, so a producer
    /// that reports a transport failure as a returned <see cref="Models.StopReason.Error"/> message -
    /// which is what <c>EmitError</c> does - is structurally invisible to it. Faulting the channel
    /// re-raises the exception inside the consumer's <c>await foreach</c>, where the existing
    /// classification, backoff and jitter machinery applies with no changes.
    /// </para>
    /// <para>
    /// The result task's exception is explicitly observed. It is normally nobody's to await once the
    /// enumeration has thrown, and an unobserved faulted task would be re-raised on the finalizer
    /// thread as a <c>TaskScheduler.UnobservedTaskException</c> - the #3382 failure mode.
    /// </para>
    /// </remarks>
    public void EndFaulted(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _done = true;
        if (_resultTcs.TrySetException(exception))
        {
            _ = _resultTcs.Task.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        _channel.Writer.TryComplete(exception);
    }

    /// <summary>
    /// Iterate over streaming events as they arrive.
    /// </summary>
    public async IAsyncEnumerator<AssistantMessageEvent> GetAsyncEnumerator(
        CancellationToken cancellationToken = default)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// Await the final AssistantMessage result.
    /// </summary>
    public Task<AssistantMessage> GetResultAsync() => _resultTcs.Task;
}

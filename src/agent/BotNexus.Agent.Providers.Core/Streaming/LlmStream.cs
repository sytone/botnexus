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

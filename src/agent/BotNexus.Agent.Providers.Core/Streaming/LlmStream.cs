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
    /// Signal the stream is complete. If result is provided and not yet set, it becomes the final result.
    /// </summary>
    public void End(AssistantMessage? result = null)
    {
        _done = true;
        if (result is not null)
            _resultTcs.TrySetResult(result);

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

using System.Collections.Concurrent;
using BotNexus.Gateway.Abstractions.Events;

namespace BotNexus.Gateway.Channels.Tests.Events;

/// <summary>
/// Real sink that records every event it is offered, in the order it saw them. Used instead
/// of a mock so the tests exercise the publisher's actual invocation and ordering behaviour
/// rather than a recorded expectation (issue #2085 TDD requirement).
/// </summary>
internal sealed class CapturingConversationEventSink : IConversationEventSink
{
    private readonly ConcurrentQueue<ConversationEvent> _received = new();

    public IReadOnlyList<ConversationEvent> Received => _received.ToArray();

    public Task OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken = default)
    {
        _received.Enqueue(conversationEvent);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Real sink modelling the common production case: an extension with no connected recipient
/// for this conversation, which returns without doing anything. It still records call counts
/// so a test can prove it was offered the event and legitimately declined to act.
/// </summary>
internal sealed class NoInterestedRecipientConversationEventSink : IConversationEventSink
{
    private int _offered;

    public int OfferedCount => Volatile.Read(ref _offered);

    public Task OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _offered);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Real sink that always throws, used to prove failure isolation without a mock framework.
/// </summary>
internal sealed class ThrowingConversationEventSink : IConversationEventSink
{
    private int _offered;

    public int OfferedCount => Volatile.Read(ref _offered);

    public Task OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _offered);
        throw new InvalidOperationException("Simulated channel extension failure.");
    }
}

/// <summary>
/// Real sink that attempts the mutations a badly behaved extension might try: swapping the
/// binding snapshot array it was handed and replacing the origin. Records what it attempted so
/// a test can assert the next sink still observes the pristine event.
/// </summary>
internal sealed class MutationAttemptingConversationEventSink : IConversationEventSink
{
    public bool AttemptedMutation { get; private set; }

    public Task OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken = default)
    {
        // Records are non-destructive: `with` produces a copy, leaving the published instance
        // untouched. That is the property under test - there is no API by which this sink could
        // corrupt what the next sink sees.
        _ = conversationEvent with { Origin = ConversationEventOrigin.None };
        _ = conversationEvent.Bindings.SetItem(0, conversationEvent.Bindings[0] with { AdapterId = "hijacked" });
        AttemptedMutation = true;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Real sink that blocks until released, used to prove a slow extension cannot stall the
/// publishing hot path or starve other sinks beyond the configured budget.
/// </summary>
internal sealed class BlockingConversationEventSink : IConversationEventSink
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public bool ObservedCancellation { get; private set; }

    public void Release() => _release.TrySetResult();

    public async Task OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken = default)
    {
        _entered.TrySetResult();
        try
        {
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ObservedCancellation = true;
            throw;
        }
    }
}

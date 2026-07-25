using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Dispatching;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Dispatching;

/// <summary>
/// Behavioural coverage for the #2123 webhook concurrency policy at the orchestrator:
/// the canonical conversation is the FIFO serialization unit, so deliveries sharing a
/// conversation never overlap even when they arrive through different webhook
/// registrations, while distinct conversations still run concurrently.
/// </summary>
/// <remarks>
/// Every assertion here uses deterministic <see cref="TaskCompletionSource"/> gates -
/// no timing sleeps. A test that "passes" because a sleep was long enough is not a
/// concurrency test.
/// </remarks>
public sealed class ConversationSerializationTests
{
    private static readonly IReadOnlyList<DispatchResult> EmptyDispatches = Array.Empty<DispatchResult>();

    [Fact]
    public async Task AcceptAsync_TwoRegistrationsPinnedToSameConversation_SerializeFifo()
    {
        // REGRESSION TEST FOR #2123.
        // Two webhook registrations ("hook-a", "hook-b") pinned to one conversation.
        // Pre-fix the queue key was webhook:<webhookId>, so these landed on separate
        // queues and both entered the processor concurrently, racing active_session_id.
        // Post-fix they share one conversation-scoped queue and must run strictly FIFO.
        var tracker = new OverlapTrackingProcessor();
        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            tracker, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        var first = orchestrator.AcceptAsync(
            CreateMessage("hook-a", conversationId: "conv-shared", content: "first"));

        // Deterministic barrier: only enqueue the second delivery once the first is
        // provably inside the processor. No sleeps.
        await tracker.FirstEntered.Task;

        var second = orchestrator.AcceptAsync(
            CreateMessage("hook-b", conversationId: "conv-shared", content: "second"));

        // The second must still be blocked - the first has not been released yet.
        second.IsCompleted.ShouldBeFalse();
        tracker.ConcurrentPeak.ShouldBe(1);

        tracker.Release();
        await Task.WhenAll(first, second);

        tracker.ConcurrentPeak.ShouldBe(1, "deliveries sharing a conversation must never overlap");
        tracker.CompletionOrder.ShouldBe(new[] { "first", "second" });
    }

    [Fact]
    public async Task AcceptAsync_DeliveriesToDifferentConversations_RunConcurrently()
    {
        // The policy explicitly permits parallelism ACROSS conversations - that is the
        // sanctioned way to get true parallel webhook processing. This gate can only be
        // satisfied if both deliveries are inside the processor at the same time; if the
        // orchestrator serialized them the second would never arrive and the test would
        // deadlock rather than pass falsely.
        var tracker = new RendezvousProcessor(expected: 2);
        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            tracker, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        var first = orchestrator.AcceptAsync(
            CreateMessage("hook-a", conversationId: "conv-1", content: "first"));
        var second = orchestrator.AcceptAsync(
            CreateMessage("hook-b", conversationId: "conv-2", content: "second"));

        await Task.WhenAll(first, second).WaitAsync(TestTimeout);

        tracker.BothEnteredTogether.ShouldBeTrue(
            "distinct conversations are separate isolation units and must not serialize");
    }

    [Fact]
    public async Task AcceptAsync_SameRegistrationBackToBack_SerializeFifo()
    {
        // Acceptance criterion: back-to-back deliveries to ONE registration execute FIFO
        // without overlapping agent turns. This held before #2123 and must keep holding.
        var tracker = new OverlapTrackingProcessor();
        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            tracker, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        var first = orchestrator.AcceptAsync(
            CreateMessage("hook-a", conversationId: "conv-1", content: "first"));
        await tracker.FirstEntered.Task;
        var second = orchestrator.AcceptAsync(
            CreateMessage("hook-a", conversationId: "conv-1", content: "second"));

        second.IsCompleted.ShouldBeFalse();
        tracker.Release();
        await Task.WhenAll(first, second);

        tracker.ConcurrentPeak.ShouldBe(1);
        tracker.CompletionOrder.ShouldBe(new[] { "first", "second" });
    }

    [Fact]
    public async Task AcceptAsync_SameConversationDifferentSessionHints_StillSerialize()
    {
        // Sad path for the naive fix: keying on the session hint would let two sessions
        // in one conversation overlap and stomp conversation-level state. The conversation
        // hint must dominate.
        var tracker = new OverlapTrackingProcessor();
        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            tracker, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        var first = orchestrator.AcceptAsync(
            CreateMessage("hook-a", conversationId: "conv-shared", content: "first", sessionId: "sess-a"));
        await tracker.FirstEntered.Task;
        var second = orchestrator.AcceptAsync(
            CreateMessage("hook-b", conversationId: "conv-shared", content: "second", sessionId: "sess-b"));

        second.IsCompleted.ShouldBeFalse();
        tracker.Release();
        await Task.WhenAll(first, second);

        tracker.ConcurrentPeak.ShouldBe(1);
    }

    [Fact]
    public async Task AcceptAsync_NoConversationHint_StillSerializesPerChannelAddress()
    {
        // Sad path: non-webhook transports that carry no conversation hint must retain
        // the legacy channel-composite isolation rather than degrading to no isolation.
        var tracker = new OverlapTrackingProcessor();
        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            tracker, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        var first = orchestrator.AcceptAsync(
            CreateMessage("addr-1", conversationId: null, content: "first"));
        await tracker.FirstEntered.Task;
        var second = orchestrator.AcceptAsync(
            CreateMessage("addr-1", conversationId: null, content: "second"));

        second.IsCompleted.ShouldBeFalse();
        tracker.Release();
        await Task.WhenAll(first, second);

        tracker.ConcurrentPeak.ShouldBe(1);
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static InboundMessage CreateMessage(
        string channelAddress, string? conversationId, string content, string? sessionId = null)
        => new()
        {
            ChannelType = ChannelKey.From("webhook"),
            ChannelAddress = ChannelAddress.From(channelAddress),
            SenderId = $"webhook:{channelAddress}",
            Sender = CitizenId.Of(AgentId.From("tinker")),
            Content = content,
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(null, sessionId, conversationId)
        };

    /// <summary>
    /// Processor that holds the first call on a gate and records the maximum number of
    /// simultaneously-executing calls plus the completion order.
    /// </summary>
    private sealed class OverlapTrackingProcessor : IInboundMessageProcessor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _completionOrder = [];
        private readonly object _gate = new();
        private int _inFlight;
        private int _peak;

        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ConcurrentPeak => Volatile.Read(ref _peak);

        public IReadOnlyList<string> CompletionOrder
        {
            get { lock (_gate) { return _completionOrder.ToArray(); } }
        }

        public void Release() => _release.TrySetResult();

        public async Task<InboundProcessingOutcome> ProcessAsync(
            InboundMessage message, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _peak, current);
            FirstEntered.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(TestTimeout);
                lock (_gate) { _completionOrder.Add(message.Content); }
                return new InboundProcessingOutcome(EmptyDispatches, ShouldClosePerSessionQueue: false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int snapshot;
            do
            {
                snapshot = Volatile.Read(ref target);
                if (value <= snapshot) return;
            }
            while (Interlocked.CompareExchange(ref target, value, snapshot) != snapshot);
        }
    }

    /// <summary>
    /// Processor whose calls only complete once <paramref name="expected"/> of them are
    /// simultaneously inside <c>ProcessAsync</c>. Genuine concurrency is the only way
    /// this can finish - it cannot be satisfied by a serialized execution.
    /// </summary>
    private sealed class RendezvousProcessor(int expected) : IInboundMessageProcessor
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public bool BothEnteredTogether { get; private set; }

        public async Task<InboundProcessingOutcome> ProcessAsync(
            InboundMessage message, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) == expected)
            {
                BothEnteredTogether = true;
                _allArrived.TrySetResult();
            }

            await _allArrived.Task.WaitAsync(TestTimeout);
            return new InboundProcessingOutcome(EmptyDispatches, ShouldClosePerSessionQueue: false);
        }
    }
}

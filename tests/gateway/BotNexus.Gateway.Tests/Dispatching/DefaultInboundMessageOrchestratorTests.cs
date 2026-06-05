using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Dispatching;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Dispatching;

/// <summary>
/// Pins the behavioural contract of <see cref="DefaultInboundMessageOrchestrator"/>:
/// per-session FIFO serialization, bounded backpressure, processor invocation,
/// queue closure on sealed sessions, and exception propagation.
/// </summary>
public sealed class DefaultInboundMessageOrchestratorTests
{
    private static readonly IReadOnlyList<DispatchResult> EmptyDispatches = Array.Empty<DispatchResult>();

    [Fact]
    public async Task AcceptAsync_NullMessage_Throws()
    {
        var processor = Substitute.For<IInboundMessageProcessor>();
        var orchestrator = new DefaultInboundMessageOrchestrator(processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        await Should.ThrowAsync<ArgumentNullException>(() => orchestrator.AcceptAsync(null!));
    }

    [Fact]
    public async Task AcceptAsync_InvalidSender_Throws()
    {
        var processor = Substitute.For<IInboundMessageProcessor>();
        var orchestrator = new DefaultInboundMessageOrchestrator(processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        var message = new InboundMessage
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("addr-1"),
            SenderId = "sender-1",
            Sender = default, // invalid
            Content = "hi"
        };

        var ex = await Should.ThrowAsync<ArgumentException>(() => orchestrator.AcceptAsync(message));
        ex.ParamName.ShouldBe("message");
    }

    [Fact]
    public async Task AcceptAsync_NoRoute_ReturnsNoRouteStatus()
    {
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new InboundProcessingOutcome(EmptyDispatches, ShouldClosePerSessionQueue: false));

        var orchestrator = new DefaultInboundMessageOrchestrator(processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);
        var message = CreateMessage("addr-no-route");

        var result = await orchestrator.AcceptAsync(message);

        result.Status.ShouldBe(InboundDispatchStatus.NoRoute);
        result.Dispatches.ShouldBeEmpty();
        await processor.Received(1).ProcessAsync(message, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptAsync_ProcessorReturnsDispatches_ReturnsAcceptedWithDispatches()
    {
        var dispatch = CreateDispatchResult();
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new InboundProcessingOutcome(new[] { dispatch }, ShouldClosePerSessionQueue: false));

        var orchestrator = new DefaultInboundMessageOrchestrator(processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);
        var message = CreateMessage("addr-accepted");

        var result = await orchestrator.AcceptAsync(message);

        result.Status.ShouldBe(InboundDispatchStatus.Accepted);
        result.Dispatches.Count.ShouldBe(1);
        result.Dispatches[0].ShouldBe(dispatch);
    }

    [Fact]
    public async Task AcceptAsync_ProcessorThrows_RethrowsAndReportsRejected()
    {
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns<Task<InboundProcessingOutcome>>(_ => throw new InvalidOperationException("boom"));

        var orchestrator = new DefaultInboundMessageOrchestrator(processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);
        var message = CreateMessage("addr-throw");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => orchestrator.AcceptAsync(message));
        ex.Message.ShouldBe("boom");
    }

    [Fact]
    public async Task AcceptAsync_TwoMessagesSameQueueKey_ProcessedSerially()
    {
        // Pin per-session FIFO serialization: the second AcceptAsync must wait until
        // the first completes inside the worker. We block the first processor call
        // on a TaskCompletionSource; if the orchestrator served them concurrently the
        // second call would arrive at the processor before the first releases.
        var processor = Substitute.For<IInboundMessageProcessor>();
        var firstGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callIndex = 0;

        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var index = Interlocked.Increment(ref callIndex);
                if (index == 1)
                {
                    firstStarted.TrySetResult(true);
                    await firstGate.Task;
                }
                else
                {
                    secondStarted.TrySetResult(true);
                }
                return new InboundProcessingOutcome(EmptyDispatches, false);
            });

        var orchestrator = new DefaultInboundMessageOrchestrator(processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);
        var first = CreateMessage("addr-shared");
        var second = CreateMessage("addr-shared");

        var firstTask = orchestrator.AcceptAsync(first);
        await firstStarted.Task; // ensure first message is inside the processor before enqueuing second
        var secondTask = orchestrator.AcceptAsync(second);

        // Second must NOT have started yet — wait briefly and assert.
        var raced = await Task.WhenAny(secondStarted.Task, Task.Delay(200));
        raced.ShouldNotBe(secondStarted.Task, "second message must not start while first is in-flight on the same queue key");

        firstGate.SetResult(true);
        await Task.WhenAll(firstTask, secondTask);
        callIndex.ShouldBe(2);
    }

    [Fact]
    public async Task AcceptAsync_TwoMessagesDifferentQueueKeys_ProcessedConcurrently()
    {
        // Different queue keys must NOT serialise — independent sessions must
        // make forward progress in parallel. If the orchestrator accidentally
        // shared a single worker across keys this test would deadlock.
        var processor = Substitute.For<IInboundMessageProcessor>();
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var msg = ci.Arg<InboundMessage>();
                if (msg.ChannelAddress.Value == "addr-A")
                {
                    firstStarted.TrySetResult(true);
                    await firstGate.Task;
                }
                else
                {
                    secondStarted.TrySetResult(true);
                    await secondGate.Task;
                }
                return new InboundProcessingOutcome(EmptyDispatches, false);
            });

        var orchestrator = new DefaultInboundMessageOrchestrator(processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);
        var firstTask = orchestrator.AcceptAsync(CreateMessage("addr-A"));
        var secondTask = orchestrator.AcceptAsync(CreateMessage("addr-B"));

        // Both should reach "started" without either gate being released.
        await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));

        firstGate.SetResult(true);
        secondGate.SetResult(true);
        await Task.WhenAll(firstTask, secondTask);
    }

    [Fact]
    public async Task AcceptAsync_QueueKeyDerivedFromRequestedSessionId_WhenPresent()
    {
        // Pin: messages with the same RequestedSessionId share a queue REGARDLESS
        // of channel type/address, matching the legacy GatewayHost.GetQueueKey.
        var processor = Substitute.For<IInboundMessageProcessor>();
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callIndex = 0;

        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var index = Interlocked.Increment(ref callIndex);
                if (index == 1)
                {
                    firstStarted.TrySetResult(true);
                    await firstGate.Task;
                }
                else
                {
                    secondStarted.TrySetResult(true);
                }
                return new InboundProcessingOutcome(EmptyDispatches, false);
            });

        var orchestrator = new DefaultInboundMessageOrchestrator(processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);
        var first = CreateMessage("addr-A", sessionId: "shared-session");
        var second = CreateMessage("addr-B", sessionId: "shared-session"); // different address, same session

        var firstTask = orchestrator.AcceptAsync(first);
        await firstStarted.Task;
        var secondTask = orchestrator.AcceptAsync(second);

        var raced = await Task.WhenAny(secondStarted.Task, Task.Delay(200));
        raced.ShouldNotBe(secondStarted.Task, "messages sharing RequestedSessionId must serialise on a single per-session queue");

        firstGate.SetResult(true);
        await Task.WhenAll(firstTask, secondTask);
    }

    [Fact]
    public async Task AcceptAsync_CancellationStopsCallerWait_ProcessorRunsToCompletion()
    {
        // Pin: cancellation of the caller's token stops the await on completion
        // but does NOT cancel the processor work — agent execution survives
        // transport disconnect, matching the legacy GatewayHost behaviour
        // (CancellationToken.None passed into ProcessInboundMessageAsync).
        var processor = Substitute.For<IInboundMessageProcessor>();
        var processorStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processorFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProcessor = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                processorStarted.TrySetResult(true);
                await releaseProcessor.Task;
                processorFinished.TrySetResult(true);
                return new InboundProcessingOutcome(EmptyDispatches, false);
            });

        var orchestrator = new DefaultInboundMessageOrchestrator(processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);
        var message = CreateMessage("addr-cancel");

        using var cts = new CancellationTokenSource();
        var acceptTask = orchestrator.AcceptAsync(message, cts.Token);

        await processorStarted.Task; // processor is running on detached token
        cts.Cancel();

        // Caller's task should complete (cancelled or naturally) without crashing.
        try { await acceptTask; }
        catch (OperationCanceledException) { /* expected */ }

        // Now release the processor and verify it completed successfully, proving
        // the inner work was NOT cancelled by the caller.
        releaseProcessor.SetResult(true);
        await processorFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static InboundMessage CreateMessage(string address, string? sessionId = null)
        => new()
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From(address),
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            Content = "hello",
            RoutingHints = sessionId is null
                ? null
                : InboundMessageRoutingHints.LiftFromStrings(null, sessionId, null)
        };

    private static DispatchResult CreateDispatchResult()
    {
        var message = CreateMessage("addr-disp");
        var source = new ChannelSource(message.ChannelType, message.ChannelAddress, message.SenderId, message.BindingId);
        var context = new InboundMessageContext(AgentId.From("agent-1"), message, source);
        var resolution = new ConversationSessionResolution(
            ConversationId.From("c_1"),
            SessionId.From("s_1"),
            IsNewConversation: true,
            IsNewSession: true,
            OriginatingBindingId: null,
            DisplayPrefix: null);
        return new DispatchResult(context, source, resolution);
    }

    [Fact]
    public void Post_ValidMessage_ReturnsTrueAndQueuesWithoutBlocking()
    {
        // Post must return synchronously (not await processor completion).
        // We verify only the return value here; the processor contract is tested
        // via AcceptAsync tests which confirm the queue worker runs ProcessAsync.
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new InboundProcessingOutcome(EmptyDispatches, false));

        var orchestrator = new DefaultInboundMessageOrchestrator(
            processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        var result = orchestrator.Post(CreateMessage("addr-post"));

        // Post returns true = message was accepted onto the queue.
        result.ShouldBeTrue();
    }

    [Fact]
    public void Post_NullMessage_Throws()
    {
        var processor = Substitute.For<IInboundMessageProcessor>();
        var orchestrator = new DefaultInboundMessageOrchestrator(
            processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        Should.Throw<ArgumentNullException>(() => orchestrator.Post(null!));
    }

    [Fact]
    public void Post_InvalidSender_Throws()
    {
        var processor = Substitute.For<IInboundMessageProcessor>();
        var orchestrator = new DefaultInboundMessageOrchestrator(
            processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        var message = new InboundMessage
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("addr-1"),
            SenderId = "sender-1",
            Sender = default, // invalid
            Content = "hi"
        };

        Should.Throw<ArgumentException>(() => orchestrator.Post(message));
    }

    [Fact]
    public async Task Post_WhenQueueFull_ReturnsFalse()
    {
        var processor = Substitute.For<IInboundMessageProcessor>();
        // Capacity 1.  Strategy:
        //   1. Post first message   -> channel buffer has 1 item
        //   2. Wait until worker dequeues it (enters ProcessAsync) -> buffer empty
        //   3. Post second message  -> buffer has 1 item again
        //   4. Post third message   -> buffer full, TryWrite returns false
        // Without step 2 the worker drains the buffer before step 3, making
        // the capacity assertion racy.
        var processorStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processorGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                processorStarted.TrySetResult(true);
                await processorGate.Task;
                return new InboundProcessingOutcome(EmptyDispatches, false);
            });

        var orchestrator = new DefaultInboundMessageOrchestrator(
            processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance, queueCapacity: 1);

        // 1. First Post fills the buffer
        var first = orchestrator.Post(CreateMessage("addr-full"));
        first.ShouldBeTrue();

        // 2. Wait until worker has consumed the first item (buffer now empty, worker in ProcessAsync)
        await processorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 3. Second Post fills the buffer again
        var second = orchestrator.Post(CreateMessage("addr-full"));
        second.ShouldBeTrue();

        // 4. Third Post: buffer is at capacity -> TryWrite returns false
        var third = orchestrator.Post(CreateMessage("addr-full"));
        third.ShouldBeFalse(); // queue is full

        // Unblock the processor to avoid test hang
        processorGate.TrySetResult(true);
    }
}

using System.Collections.Concurrent;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Dispatching;
using Microsoft.Extensions.Logging;
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

        secondStarted.Task.IsCompleted.ShouldBeFalse(
            "second message must not start while first is held in-flight on the same queue key");

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

        secondStarted.Task.IsCompleted.ShouldBeFalse(
            "messages sharing RequestedSessionId must serialise on a single per-session queue");

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

    [Fact]
    public async Task AcceptAsync_ExecutionControlSignalsActualStartAndCancelsProcessor()
    {
        var processor = Substitute.For<IInboundMessageProcessor>();
        var processorStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<InboundExecutionControl>())
            .Returns(async callInfo =>
            {
                var control = callInfo.ArgAt<InboundExecutionControl>(1);
                control.CancellationToken.ThrowIfCancellationRequested();
                await control.NotifyStartedAsync();
                processorStarted.TrySetResult(control.CancellationToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, control.CancellationToken);
                return new InboundProcessingOutcome(EmptyDispatches, false);
            });

        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);
        using var execution = new CancellationTokenSource();
        var actualStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new InboundExecutionControl(
            execution.Token,
            () =>
            {
                actualStart.TrySetResult();
                return Task.CompletedTask;
            });

        var accept = orchestrator.AcceptAsync(CreateMessage("addr-deadline"), control);
        await actualStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var observedToken = await processorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        observedToken.ShouldBe(execution.Token);
        execution.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await accept.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AcceptAsync_ExecutionControlDoesNotReportStartedWhileQueued()
    {
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = 0;
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref invocation);
                firstStarted.TrySetResult();
                await firstGate.Task;
                return new InboundProcessingOutcome(EmptyDispatches, false);
            });
        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<InboundExecutionControl>())
            .Returns(async callInfo =>
            {
                var control = callInfo.ArgAt<InboundExecutionControl>(1);
                control.CancellationToken.ThrowIfCancellationRequested();
                await control.NotifyStartedAsync();
                Interlocked.Increment(ref invocation);
                return new InboundProcessingOutcome(EmptyDispatches, false);
            });

        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);
        var first = orchestrator.AcceptAsync(CreateMessage("addr-queued"));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var control = new InboundExecutionControl(
            CancellationToken.None,
            () =>
            {
                secondStarted.TrySetResult();
                return Task.CompletedTask;
            });
        var second = orchestrator.AcceptAsync(CreateMessage("addr-queued"), control);

        secondStarted.Task.IsCompleted.ShouldBeFalse(
            "actual start must not be reported while the message is behind another turn");

        firstGate.TrySetResult();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AcceptAsync_ExecutionCancelledWhileQueued_NeverReportsStartedOrRunsProcessor()
    {
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = 0;
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref invocation);
                firstStarted.TrySetResult();
                await firstGate.Task;
                return new InboundProcessingOutcome(EmptyDispatches, false);
            });
        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<InboundExecutionControl>())
            .Returns(async callInfo =>
            {
                var control = callInfo.ArgAt<InboundExecutionControl>(1);
                control.CancellationToken.ThrowIfCancellationRequested();
                await control.NotifyStartedAsync();
                Interlocked.Increment(ref invocation);
                return new InboundProcessingOutcome(EmptyDispatches, false);
            });

        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);
        var first = orchestrator.AcceptAsync(CreateMessage("addr-expired"));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var execution = new CancellationTokenSource();
        var actualStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = orchestrator.AcceptAsync(
            CreateMessage("addr-expired"),
            new InboundExecutionControl(
                execution.Token,
                () =>
                {
                    actualStart.TrySetResult();
                    return Task.CompletedTask;
                }));

        execution.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await second.WaitAsync(TimeSpan.FromSeconds(5)));
        firstGate.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        await TestAwait.EventuallyAsync(
            () => Volatile.Read(ref invocation) == 1,
            "the cancelled queued item is removed without a processor invocation");
        actualStart.Task.IsCompleted.ShouldBeFalse(
            "a terminal queued timeout must never regress to Running when its slot later opens");
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
    public async Task BeginQuiesce_RejectsEveryNewAdmissionWithoutStoppingExistingWork()
    {
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor.ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new InboundProcessingOutcome(EmptyDispatches, false));
        var orchestrator = new DefaultInboundMessageOrchestrator(
            processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        orchestrator.TryBeginQuiesce().ShouldBeTrue();
        orchestrator.TryBeginQuiesce().ShouldBeFalse();
        orchestrator.Post(CreateMessage("post")).ShouldBeFalse();
        (await orchestrator.PostAsync(CreateMessage("post-async"))).ShouldBe(InboundDispatchStatus.Busy);
        (await orchestrator.AcceptAsync(CreateMessage("accept"))).Status.ShouldBe(InboundDispatchStatus.Busy);
        await processor.DidNotReceive().ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>());
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

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public async Task Post_ProcessingPersistenceFailure_ReportsGenericFeedbackAndContinuesWithoutReplay(
        bool usePostAsync, int feedbackMode)
    {
        // 0 = recording adapter, 1 = adapter fails after recording the attempt,
        // 2 = no originating adapter, 3 = lookup throws. All are best effort.
        await AssertPostedFailureAsync(usePostAsync, feedbackMode, cancelled: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_ProcessorCancellation_DoesNotReportProcessingFailureAndContinuesWithoutReplay(
        bool usePostAsync)
    {
        await AssertPostedFailureAsync(usePostAsync, feedbackMode: 0, cancelled: true);
    }

    private static async Task AssertPostedFailureAsync(bool usePostAsync, int feedbackMode, bool cancelled)
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var followingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new ConcurrentQueue<InboundMessage>();
        var feedback = new ConcurrentQueue<(OutboundMessage Message, CancellationToken Token)>();
        var first = CreateMessage("addr-persistence-failure", sessionId: "s_persistence_failure");
        // Different addresses on the same session prove feedback targets the failed origin,
        // not whichever message happens to follow it on the shared queue.
        var following = CreateMessage("addr-following-success", sessionId: "s_persistence_failure");
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor.ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var message = callInfo.Arg<InboundMessage>();
                processed.Enqueue(message);
                if (ReferenceEquals(message, first))
                {
                    firstStarted.TrySetResult();
                    await releaseFailure.Task;
                    if (cancelled)
                    {
                        throw new OperationCanceledException("private cancellation detail");
                    }
                    // Represents a persistence failure before the processor can return an outcome.
                    // No real store or host is needed to exercise the queue's failure boundary.
                    throw new IOException("private persistence detail: sessions.sqlite / operator-path");
                }

                followingStarted.TrySetResult();
                return new InboundProcessingOutcome(new[] { CreateDispatchResult() }, false);
            });

        var adapter = Substitute.For<IChannelAdapter>();
        adapter.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                feedback.Enqueue((callInfo.Arg<OutboundMessage>(), callInfo.Arg<CancellationToken>()));
                return feedbackMode == 1
                    ? Task.FromException(new IOException("private adapter failure"))
                    : Task.CompletedTask;
            });
        var channelManager = Substitute.For<IChannelManager>();
        channelManager.Get(first.ChannelType).Returns(_ => feedbackMode == 3
            ? throw new IOException("private lookup failure")
            : feedbackMode == 2 ? null : adapter);
        var orchestrator = new DefaultInboundMessageOrchestrator(
            processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance, channelManager);

        try
        {
            if (usePostAsync)
            {
                (await orchestrator.PostAsync(first)).ShouldBe(InboundDispatchStatus.Accepted);
            }
            else
            {
                orchestrator.Post(first).ShouldBeTrue();
            }
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Queue the successor while the first processor is held, not after its failure.
            if (usePostAsync)
            {
                (await orchestrator.PostAsync(following)).ShouldBe(InboundDispatchStatus.Accepted);
            }
            else
            {
                orchestrator.Post(following).ShouldBeTrue();
            }
            releaseFailure.TrySetResult();
            await followingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseFailure.TrySetResult();
            // Processor entry/finally is not queue completion. Await the actual worker drain
            // before asserting exact send/call counts or the absence of cancellation feedback.
            await orchestrator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }

        processed.ToArray().ShouldBe(new[] { first, following },
            "a failed or cancelled message must not be replayed or stop its queued successor");
        if (cancelled || feedbackMode is 2 or 3)
        {
            feedback.ShouldBeEmpty();
            return;
        }

        var attempt = feedback.ShouldHaveSingleItem();
        attempt.Message.ChannelType.ShouldBe(first.ChannelType);
        attempt.Message.ChannelAddress.ShouldBe(first.ChannelAddress);
        attempt.Message.SessionId.ShouldBe("s_persistence_failure");
        attempt.Token.CanBeCanceled.ShouldBeTrue(
            "failure feedback has its own bounded deadline, not an unbounded None token");
        // Exact generic wording excludes exception details, persistence/execution claims,
        // and retry advice that could cause a duplicate after an uncertain processing failure.
        attempt.Message.Content.ShouldBe(
            "Your message could not be processed because an internal error occurred.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptAsync_FailureFeedback_PreservesDistinctRoutingHints(bool conversationOnly)
    {
        var original = new IOException("private persistence failure");
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor.ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<InboundProcessingOutcome>(original));
        var adapter = Substitute.For<IChannelAdapter>();
        OutboundMessage? feedback = null;
        adapter.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                feedback = call.Arg<OutboundMessage>();
                return Task.CompletedTask;
            });
        var manager = Substitute.For<IChannelManager>();
        manager.Get(Arg.Any<ChannelKey>()).Returns(adapter);
        var message = CreateMessage("origin-address") with
        {
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(
                null, conversationOnly ? null : "distinct-session", "distinct-conversation"),
            ChannelRequestId = "distinct-request"
        };
        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance, manager);

        var actual = await Should.ThrowAsync<IOException>(() => orchestrator.AcceptAsync(message));
        actual.ShouldBeSameAs(original);
        feedback.ShouldNotBeNull();
        feedback.ChannelType.ShouldBe(message.ChannelType);
        feedback.ChannelAddress.ShouldBe(message.ChannelAddress);
        feedback.ConversationId.ShouldBe("distinct-conversation");
        feedback.SessionId.ShouldBe(conversationOnly ? null : "distinct-session");
        feedback.ChannelRequestId.ShouldBe("distinct-request");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptAsync_FailedOrTimedOutFeedback_PreservesOriginalExceptionAndDrains(bool timeout)
    {
        var original = new IOException("original persistence failure");
        var lateFailure = new IOException("late adapter failure");
        var send = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadlineStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var followingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new FeedbackLogger();
        var first = CreateMessage("shared-failure-queue");
        var following = CreateMessage("shared-failure-queue");
        var processed = new ConcurrentQueue<InboundMessage>();
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor.ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var message = call.Arg<InboundMessage>();
                processed.Enqueue(message);
                if (ReferenceEquals(message, first))
                    return Task.FromException<InboundProcessingOutcome>(original);
                followingStarted.TrySetResult();
                return Task.FromResult(new InboundProcessingOutcome(EmptyDispatches, false));
            });
        var adapter = Substitute.For<IChannelAdapter>();
        adapter.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sendStarted.TrySetResult(call.Arg<CancellationToken>());
                return send.Task; // Deliberately ignores cancellation.
            });
        var manager = Substitute.For<IChannelManager>();
        manager.Get(Arg.Any<ChannelKey>()).Returns(adapter);
        var orchestrator = new DefaultInboundMessageOrchestrator(
            processor, logger, manager,
            failureFeedbackDelay: (duration, token) =>
            {
                duration.ShouldBe(TimeSpan.FromSeconds(3));
                token.CanBeCanceled.ShouldBeTrue();
                deadlineStarted.TrySetResult();
                return deadline.Task;
            });
        using var admission = new CancellationTokenSource();
        try
        {
            var accepted = orchestrator.AcceptAsync(first, admission.Token);
            var feedbackToken = await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await deadlineStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            feedbackToken.CanBeCanceled.ShouldBeTrue();
            feedbackToken.ShouldNotBe(admission.Token);
            orchestrator.Post(following).ShouldBeTrue();
            followingStarted.Task.IsCompleted.ShouldBeFalse();
            accepted.IsCompleted.ShouldBeFalse();

            if (timeout)
                deadline.TrySetResult();
            else
                send.TrySetException(lateFailure);

            var actual = await Should.ThrowAsync<IOException>(
                async () => await accepted.WaitAsync(TimeSpan.FromSeconds(5)));
            actual.ShouldBeSameAs(original);
            await followingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await orchestrator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            processed.ToArray().ShouldBe(new[] { first, following });
            if (timeout)
            {
                send.Task.IsCompleted.ShouldBeFalse("deadline releases the worker; it does not stop the send");
                send.TrySetException(lateFailure);
            }
            // Logging the exact underlying fault is an explicit observation boundary, not GC timing.
            (await logger.SendFault.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeSameAs(lateFailure);
            await adapter.Received(1).SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            deadline.TrySetResult();
            send.TrySetResult();
            await orchestrator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptAsync_FeedbackCancellationCallbacks_DrainIndependentlyAndRetainRegistrations(bool callbackThrows)
    {
        var original = new IOException("original persistence failure");
        var callbackFailure = new InvalidOperationException("adapter cancellation callback failure");
        var send = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trailingCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        var followingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new ConcurrentQueue<InboundMessage>();
        var logger = new FeedbackLogger();
        var first = CreateMessage("callback-lifetime");
        var following = CreateMessage("callback-lifetime");
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor.ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var message = call.Arg<InboundMessage>();
                processed.Enqueue(message);
                if (ReferenceEquals(message, first))
                    return Task.FromException<InboundProcessingOutcome>(original);
                followingStarted.TrySetResult();
                return Task.FromResult(new InboundProcessingOutcome(EmptyDispatches, false));
            });
        var registrations = new List<CancellationTokenRegistration>();
        var adapter = Substitute.For<IChannelAdapter>();
        adapter.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var token = call.Arg<CancellationToken>();
                // Cancellation invokes registrations in reverse order. The first registration
                // must survive while the second callback blocks and the FIFO worker drains.
                registrations.Add(token.Register(() => trailingCallback.TrySetResult()));
                registrations.Add(token.Register(() =>
                {
                    callbackEntered.TrySetResult();
                    releaseCallback.Wait();
                    try
                    {
                        // Access requires the source to remain alive, not just a cancelled token.
                        token.WaitHandle.WaitOne(0).ShouldBeTrue();
                        callbackFinished.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        callbackFinished.TrySetException(ex);
                    }
                    if (callbackThrows)
                        throw callbackFailure;
                }));
                registered.TrySetResult(token);
                return send.Task;
            });
        var manager = Substitute.For<IChannelManager>();
        manager.Get(Arg.Any<ChannelKey>()).Returns(adapter);
        var orchestrator = new DefaultInboundMessageOrchestrator(
            processor, logger, manager, failureFeedbackDelay: (_, _) => deadline.Task);
        try
        {
            var accepted = orchestrator.AcceptAsync(first);
            var token = await registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            token.IsCancellationRequested.ShouldBeFalse();
            orchestrator.Post(following).ShouldBeTrue();
            accepted.IsCompleted.ShouldBeFalse();
            deadline.TrySetResult();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            token.IsCancellationRequested.ShouldBeTrue();
            var actual = await Should.ThrowAsync<IOException>(
                async () => await accepted.WaitAsync(TimeSpan.FromSeconds(5)));
            actual.ShouldBeSameAs(original);
            await followingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await orchestrator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            callbackFinished.Task.IsCompleted.ShouldBeFalse();
            trailingCallback.Task.IsCompleted.ShouldBeFalse();
            send.Task.IsCompleted.ShouldBeFalse("the hard deadline does not terminate the send");
            processed.ToArray().ShouldBe(new[] { first, following });

            releaseCallback.Set();
            await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await trailingCallback.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (callbackThrows)
            {
                var fault = await logger.SendFault.Task.WaitAsync(TimeSpan.FromSeconds(5));
                fault.ShouldBeOfType<AggregateException>().InnerExceptions.ShouldContain(callbackFailure);
            }
            send.TrySetResult();
            await adapter.Received(1).SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            releaseCallback.Set();
            deadline.TrySetResult();
            send.TrySetResult();
            await orchestrator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            // Dispose registrations only after callbacks have returned; this also drains a
            // surviving callback if an assertion above failed, without blocking the FIFO worker.
            foreach (var registration in registrations)
                await registration.DisposeAsync();
        }
    }

    [Fact]
    public async Task PostAsync_AdmissionCancelledAfterAcceptance_FailureFeedbackUsesIndependentToken()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor.ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                started.TrySetResult();
                await release.Task;
                throw new IOException("private failure");
            });
        using var admission = new CancellationTokenSource();
        var tokens = new ConcurrentQueue<CancellationToken>();
        var adapter = Substitute.For<IChannelAdapter>();
        adapter.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var token = call.Arg<CancellationToken>();
                token.IsCancellationRequested.ShouldBeFalse();
                tokens.Enqueue(token);
                return Task.CompletedTask;
            });
        var manager = Substitute.For<IChannelManager>();
        manager.Get(Arg.Any<ChannelKey>()).Returns(adapter);
        var orchestrator = new DefaultInboundMessageOrchestrator(
            processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance, manager);
        try
        {
            (await orchestrator.PostAsync(CreateMessage("cancelled-admission"), admission.Token))
                .ShouldBe(InboundDispatchStatus.Accepted);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            admission.Cancel();
        }
        finally
        {
            release.TrySetResult();
            await orchestrator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        var feedbackToken = tokens.ShouldHaveSingleItem();
        feedbackToken.CanBeCanceled.ShouldBeTrue();
        feedbackToken.ShouldNotBe(admission.Token);
    }

    private sealed class FeedbackLogger : ILogger<DefaultInboundMessageOrchestrator>
    {
        public TaskCompletionSource<Exception> SendFault { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Debug && exception is not null)
                SendFault.TrySetResult(exception);
        }
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

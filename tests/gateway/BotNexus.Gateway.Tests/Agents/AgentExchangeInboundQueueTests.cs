using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Pins issue <c>#3494</c>: <c>agent_converse</c> had no inbound queue, so a second inbound
/// exchange arriving at a busy in-process (single-slot) agent was neither queued nor refused.
/// It minted an exchange session, blocked on the agent's internal run lock, and was eventually
/// killed by the CALLER's bounded timeout - surfacing to the user as a bare
/// <c>task was canceled</c> and leaving a one-row <c>Active</c> session behind.
/// </summary>
/// <remarks>
/// <para>
/// Each test maps to one acceptance clause of #3494:
/// </para>
/// <list type="number">
///   <item>AC1 - a busy target enqueues rather than drops, and dispatches when the slot frees.</item>
///   <item>AC2 - the mailbox is bounded; overflow is an explicit backpressure result.</item>
///   <item>AC3 - a caller timeout that elapses while still queued is distinguishable from one
///   that elapses after dispatch.</item>
///   <item>AC4 - no stranded one-row <c>Active</c> session survives a caller timeout unmarked.</item>
/// </list>
/// <para>
/// Every assertion fails against the pre-fix commit: <c>AgentExchangeInboundQueue</c>,
/// <c>AgentExchangeBackpressureException</c> and <c>AgentExchangeNotDispatchedException</c> did
/// not exist, and <c>ConverseAsync</c> had no admission gate at all.
/// </para>
/// <para>
/// <strong>No finite wall-clock waits.</strong> Every rendezvous here is signal-driven - the
/// harness signals prompt arrival, and the queue's own <c>WaitingCountChanged</c> event signals
/// mailbox depth. Sleeping for a fixed interval and hoping the system got there is exactly the
/// flake class <c>TestDelayFlakeFenceTests</c> exists to keep out of this suite.
/// </para>
/// </remarks>
public sealed class AgentExchangeInboundQueueTests
{
    private static readonly AgentId Initiator = AgentId.From("test-agent");
    private static readonly AgentId Target = AgentId.From("agent-c");
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    // ---------------------------------------------------------------------------------------
    // Queue primitive: depth accounting and admission decisions in isolation.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AcquireAsync_WhenSlotFree_AdmitsImmediately_AndLeavesNoWaiters()
    {
        var queue = new AgentExchangeInboundQueue(Options.Create(new AgentExchangeOptions()));

        using var lease = await queue.AcquireAsync(Target, CancellationToken.None);

        queue.WaitingCount(Target).ShouldBe(0,
            "the holder of the slot is in flight, not waiting - only genuinely blocked callers count "
            + "toward the bound, otherwise a single uncontended exchange would consume queue depth.");
    }

    [Fact]
    public async Task AcquireAsync_WhenSlotBusy_QueuesTheSecondCaller_ThenAdmitsItOnRelease()
    {
        var queue = new AgentExchangeInboundQueue(Options.Create(new AgentExchangeOptions()));

        var first = await queue.AcquireAsync(Target, CancellationToken.None);
        var second = queue.AcquireAsync(Target, CancellationToken.None);

        await WaitForWaitersAsync(queue, Target, 1);
        second.IsCompleted.ShouldBeFalse(
            "the second acquisition must WAIT for the single slot rather than run alongside the "
            + "first. Observing it REGISTERED AS A WAITER is positive proof it is parked in the "
            + "mailbox - stronger evidence than merely failing to finish inside a sleep window.");

        first.Dispose();

        var lease = await second.WaitAsync(Generous);
        lease.ShouldNotBeNull("releasing the slot must dispatch the queued caller, not drop it.");
        lease.Dispose();
        queue.WaitingCount(Target).ShouldBe(0);
    }

    [Fact]
    public async Task AcquireAsync_WhenQueueDepthExceeded_ThrowsBackpressure_NotCancellation()
    {
        var queue = new AgentExchangeInboundQueue(Options.Create(new AgentExchangeOptions
        {
            MaxInboundQueueDepth = 1
        }));

        using var holder = await queue.AcquireAsync(Target, CancellationToken.None);
        var queued = queue.AcquireAsync(Target, CancellationToken.None);
        await WaitForWaitersAsync(queue, Target, 1);

        var overflow = await Should.ThrowAsync<AgentExchangeBackpressureException>(
            async () => await queue.AcquireAsync(Target, CancellationToken.None));

        overflow.TargetId.ShouldBe(Target);
        overflow.MaxQueueDepth.ShouldBe(1);
        overflow.ShouldNotBeAssignableTo<OperationCanceledException>(
            "AC2: overflow is an explicit refusal the caller can act on, NOT a cancellation. If this "
            + "becomes an OperationCanceledException it is indistinguishable from the very timeout "
            + "this issue exists to eliminate.");

        holder.Dispose();
        (await queued.WaitAsync(Generous)).Dispose();
    }

    [Fact]
    public async Task AcquireAsync_WhenCancelledWhileQueued_ThrowsNotDispatched()
    {
        var queue = new AgentExchangeInboundQueue(Options.Create(new AgentExchangeOptions()));
        using var cts = new CancellationTokenSource();

        using var holder = await queue.AcquireAsync(Target, CancellationToken.None);
        var queued = queue.AcquireAsync(Target, cts.Token);
        await WaitForWaitersAsync(queue, Target, 1);

        await cts.CancelAsync();

        var ex = await Should.ThrowAsync<AgentExchangeNotDispatchedException>(async () => await queued);
        ex.TargetId.ShouldBe(Target);

        queue.WaitingCount(Target).ShouldBe(0,
            "a cancelled waiter must release its queue slot, otherwise the bound leaks and the "
            + "mailbox wedges shut after MaxInboundQueueDepth abandoned callers.");
    }

    [Fact]
    public async Task AcquireAsync_SlotsAreIsolatedPerTargetAgent()
    {
        var queue = new AgentExchangeInboundQueue(Options.Create(new AgentExchangeOptions()));
        var other = AgentId.From("agent-d");

        using var busy = await queue.AcquireAsync(Target, CancellationToken.None);
        using var free = await queue.AcquireAsync(other, CancellationToken.None).WaitAsync(Generous);

        free.ShouldNotBeNull(
            "the mailbox is PER AGENT: one busy agent must never gate exchanges with a different agent.");
    }

    // ---------------------------------------------------------------------------------------
    // AC1 - enqueue instead of dropping, and dispatch when the slot frees.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ConverseAsync_WhenTargetSlotBusy_QueuesSecondExchange_AndDispatchesItWhenSlotFrees()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = CreateHarness(async (ordinal, message, _) =>
        {
            if (ordinal == 1)
                await gate.Task;
            return message + "-answered";
        });

        var firstCall = harness.Service.ConverseAsync(BuildRequest("first"), CancellationToken.None);
        await harness.WaitForPromptsAsync(1);

        var secondCall = harness.Service.ConverseAsync(BuildRequest("second"), CancellationToken.None);
        await WaitForWaitersAsync(harness.Queue, Target, 1);

        secondCall.IsCompleted.ShouldBeFalse(
            "AC1: while the target's single slot is busy the second exchange must WAIT in the mailbox.");
        harness.Prompts.Count.ShouldBe(1,
            "the queued exchange must not reach the target agent while the slot is still held.");

        gate.SetResult();

        var firstResult = await firstCall.WaitAsync(Generous);
        var secondResult = await secondCall.WaitAsync(Generous);

        firstResult.FinalResponse.ShouldBe("first-answered");
        secondResult.FinalResponse.ShouldBe("second-answered",
            "AC1: the queued exchange must be DISPATCHED once the slot frees - not dropped, and not "
            + "answered with the first exchange's response.");
        harness.Prompts.ShouldBe(["first", "second"], "the mailbox is FIFO.");
    }

    // ---------------------------------------------------------------------------------------
    // AC2 - bounded mailbox, explicit backpressure on overflow.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ConverseAsync_WhenMailboxFull_ReturnsBackpressure_NotCancellation()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = CreateHarness(
            async (ordinal, message, _) =>
            {
                if (ordinal == 1)
                    await gate.Task;
                return message + "-answered";
            },
            new AgentExchangeOptions { MaxInboundQueueDepth = 1 });

        var firstCall = harness.Service.ConverseAsync(BuildRequest("first"), CancellationToken.None);
        await harness.WaitForPromptsAsync(1);

        var queuedCall = harness.Service.ConverseAsync(BuildRequest("second"), CancellationToken.None);
        await WaitForWaitersAsync(harness.Queue, Target, 1);

        var ex = await Should.ThrowAsync<AgentExchangeBackpressureException>(
            async () => await harness.Service.ConverseAsync(BuildRequest("third"), CancellationToken.None));

        ex.Message.Contains(Target.Value, StringComparison.Ordinal).ShouldBeTrue(
            "AC2: the refusal must name the agent that is saturated so the caller can act on it.");
        ex.ShouldNotBeAssignableTo<OperationCanceledException>();

        var sessions = await harness.SessionStore.GetExistenceAsync(Initiator, new ExistenceQuery());
        sessions.Count.ShouldBe(1,
            "a refused exchange mints NO session - and neither does one still waiting in the mailbox, "
            + "because the admission gate runs BEFORE session creation. Only the exchange actually "
            + "holding the slot owns a session. This is AC4's guarantee seen from the other side: a "
            + "queued or refused exchange cannot strand a session because it never created one.");

        gate.SetResult();
        await firstCall.WaitAsync(Generous);
        await queuedCall.WaitAsync(Generous);
    }

    // ---------------------------------------------------------------------------------------
    // AC3 - "never dispatched" is distinguishable from "dispatched and cancelled".
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ConverseAsync_WhenCallerTimesOutWhileStillQueued_ReportsNeverDispatched()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = CreateHarness(async (ordinal, message, _) =>
        {
            if (ordinal == 1)
                await gate.Task;
            return message + "-answered";
        });

        var firstCall = harness.Service.ConverseAsync(BuildRequest("first"), CancellationToken.None);
        await harness.WaitForPromptsAsync(1);

        using var callerCts = new CancellationTokenSource();
        var queuedCall = harness.Service.ConverseAsync(BuildRequest("second"), callerCts.Token);
        await WaitForWaitersAsync(harness.Queue, Target, 1);
        await callerCts.CancelAsync();

        var ex = await Should.ThrowAsync<AgentExchangeNotDispatchedException>(async () => await queuedCall);

        ex.TargetId.ShouldBe(Target);
        ex.Message.Contains("never dispatched", StringComparison.Ordinal).ShouldBeTrue(
            "AC3: the caller must be able to tell 'my message never reached the agent' from 'the "
            + "agent was working on it and I gave up'. A bare 'task was canceled' is exactly the "
            + "symptom #3494 was filed for.");
        ex.ShouldNotBeAssignableTo<OperationCanceledException>(
            "this distinction has to SURVIVE the await. A cancellation-derived exception is "
            + "converted by the async machinery into a plain TaskCanceledException before the "
            + "caller ever sees it, which would silently restore the very ambiguity being fixed.");

        gate.SetResult();
        await firstCall.WaitAsync(Generous);
    }

    [Fact]
    public async Task ConverseAsync_WhenCallerTimesOutAfterDispatch_IsNotReportedAsNeverDispatched()
    {
        using var callerCts = new CancellationTokenSource();
        var harness = CreateHarness((_, _, ct) =>
        {
            callerCts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("unreachable");
        });

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await harness.Service.ConverseAsync(BuildRequest("first"), callerCts.Token));

        harness.Prompts.Count.ShouldBe(1,
            "AC3 control case: this exchange DID reach the agent, so it must stay an ordinary "
            + "cancellation. Reporting it as never-dispatched would trade one misleading diagnosis "
            + "for another - and the Should.ThrowAsync above proves it did not, since "
            + "AgentExchangeNotDispatchedException is deliberately not an OperationCanceledException.");
    }

    // ---------------------------------------------------------------------------------------
    // AC4 - no unmarked one-row Active session survives a caller timeout.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ConverseAsync_WhenCallerTimesOutWhileQueued_LeavesNoStrandedSession()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = CreateHarness(async (ordinal, message, _) =>
        {
            if (ordinal == 1)
                await gate.Task;
            return message + "-answered";
        });

        var firstCall = harness.Service.ConverseAsync(BuildRequest("first"), CancellationToken.None);
        await harness.WaitForPromptsAsync(1);

        using var callerCts = new CancellationTokenSource();
        var queuedCall = harness.Service.ConverseAsync(BuildRequest("second"), callerCts.Token);
        await WaitForWaitersAsync(harness.Queue, Target, 1);
        await callerCts.CancelAsync();

        await Should.ThrowAsync<AgentExchangeNotDispatchedException>(async () => await queuedCall);

        var sessions = await harness.SessionStore.GetExistenceAsync(Initiator, new ExistenceQuery());
        sessions.Count.ShouldBe(1,
            "AC4: admission happens BEFORE the session is minted, so a caller that times out while "
            + "queued leaves nothing behind. The reporter's four one-row Active sessions are exactly "
            + "the artefact this ordering eliminates - the only surviving session here belongs to the "
            + "exchange that genuinely holds the slot.");

        gate.SetResult();
        await firstCall.WaitAsync(Generous);
    }

    [Fact]
    public async Task ConverseAsync_WhenCallerTimesOutAfterDispatch_MarksTheSessionWithoutSealingIt()
    {
        using var callerCts = new CancellationTokenSource();
        var harness = CreateHarness((_, _, ct) =>
        {
            callerCts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("unreachable");
        });

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await harness.Service.ConverseAsync(BuildRequest("first"), callerCts.Token));

        var sessions = await harness.SessionStore.GetExistenceAsync(Initiator, new ExistenceQuery());
        sessions.Count.ShouldBe(1);
        var session = await harness.SessionStore.GetAsync(sessions[0].SessionId);
        session.ShouldNotBeNull();

        session!.Status.ShouldBe(GatewaySessionStatus.Active,
            "#553 still holds: caller cancellation must NOT seal, so the caller can retry. AC4 is "
            + "satisfied by MARKING the session, which is the option the acceptance clause offers "
            + "alongside sealing precisely because sealing would break #553.");
        session.Metadata.ContainsKey("error").ShouldBeFalse(
            "the error key belongs to the seal-on-failure arm; a caller timeout is not a failure.");
        session.Metadata.TryGetValue("exchangeOutcome", out var outcome).ShouldBeTrue(
            "AC4: a session abandoned by a caller timeout must carry an outcome marker so a reaper "
            + "(or an operator) can find it. Before #3494 it was indistinguishable from a healthy "
            + "in-flight exchange.");
        (outcome as string).ShouldBe("callerCancelled");
    }

    // ---------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------

    private static AgentExchangeRequest BuildRequest(string message) => new()
    {
        InitiatorId = Initiator,
        TargetId = Target,
        Message = message,
        MaxTurns = 1
    };

    /// <summary>
    /// A wired <see cref="AgentExchangeService"/> plus the collaborators a test needs to observe:
    /// the queue (for mailbox depth), the session store (for AC4), and the prompt log with an
    /// arrival signal (so tests rendezvous on an event rather than a sleep).
    /// </summary>
    private sealed class Harness(
        AgentExchangeInboundQueue queue,
        InMemorySessionStore sessionStore)
    {
        private readonly List<string> _prompts = [];
        private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
        private readonly object _sync = new();

        /// <summary>
        /// Assigned once during construction. The service and the stub agent handle are mutually
        /// referential - the handle records prompts into this harness, and the harness exposes the
        /// service that drives the handle - so one of the two has to be filled in after the other.
        /// </summary>
        public AgentExchangeService Service { get; set; } = null!;

        public AgentExchangeInboundQueue Queue { get; } = queue;
        public InMemorySessionStore SessionStore { get; } = sessionStore;

        /// <summary>A snapshot of the prompts that reached the target agent, in arrival order.</summary>
        public IReadOnlyList<string> Prompts
        {
            get { lock (_sync) return [.. _prompts]; }
        }

        /// <summary>Records an arriving prompt and returns its 1-based ordinal.</summary>
        public int RecordPrompt(string message)
        {
            List<TaskCompletionSource> ready = [];
            int ordinal;
            lock (_sync)
            {
                _prompts.Add(message);
                ordinal = _prompts.Count;
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].Count > ordinal)
                        continue;
                    ready.Add(_waiters[i].Signal);
                    _waiters.RemoveAt(i);
                }
            }

            foreach (var signal in ready)
                signal.TrySetResult();
            return ordinal;
        }

        /// <summary>Completes once <paramref name="expected"/> prompts have reached the agent.</summary>
        public Task WaitForPromptsAsync(int expected)
        {
            TaskCompletionSource signal;
            lock (_sync)
            {
                if (_prompts.Count >= expected)
                    return Task.CompletedTask;
                signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((expected, signal));
            }

            return signal.Task.WaitAsync(Generous);
        }
    }

    private static Harness CreateHarness(
        Func<int, string, CancellationToken, Task<string>> respond,
        AgentExchangeOptions? exchangeOptions = null)
    {
        var options = exchangeOptions ?? new AgentExchangeOptions();
        var sessionStore = new InMemorySessionStore();
        var conversationStore = new InMemoryConversationStore();
        var queue = new AgentExchangeInboundQueue(Options.Create(options));

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(Initiator)).Returns(new AgentDescriptor
        {
            AgentId = Initiator,
            DisplayName = "Initiator",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot",
            SubAgentIds = [Target.Value]
        });
        registry.Setup(r => r.Contains(Target)).Returns(true);

        var harness = new Harness(queue, sessionStore);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string message, CancellationToken ct) =>
            {
                var ordinal = harness.RecordPrompt(message);
                return new AgentResponse { Content = await respond(ordinal, message, ct) };
            });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        harness.Service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            conversationStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance,
            exchangeOptions: Options.Create(options),
            inboundQueue: queue);

        return harness;
    }

    /// <summary>
    /// Completes when <paramref name="expected"/> exchanges are registered as WAITING for the
    /// target's slot, driven by the queue's own <c>WaitingCountChanged</c> event.
    /// </summary>
    /// <remarks>
    /// Signal-driven, not poll-driven. The post-subscribe re-check is load-bearing: the depth may
    /// already have been reached before the handler attached, and a wait that can miss its own
    /// condition is a hang rather than a flake.
    /// </remarks>
    private static async Task WaitForWaitersAsync(AgentExchangeInboundQueue queue, AgentId target, int expected)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChanged(AgentId changed, int waiting)
        {
            if (changed == target && waiting >= expected)
                reached.TrySetResult();
        }

        queue.WaitingCountChanged += OnChanged;
        try
        {
            if (queue.WaitingCount(target) >= expected)
                reached.TrySetResult();
            await reached.Task.WaitAsync(Generous);
        }
        finally
        {
            queue.WaitingCountChanged -= OnChanged;
        }
    }
}

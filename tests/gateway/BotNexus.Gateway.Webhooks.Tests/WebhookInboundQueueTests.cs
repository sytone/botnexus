using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Webhooks;

namespace BotNexus.Gateway.Webhooks.Tests;

/// <summary>
/// Admission-control coverage for the per-agent inbound webhook queue (#3851).
/// </summary>
/// <remarks>
/// Every concurrency assertion uses a deterministic gate rather than a timing sleep: the point of
/// the type is that a delivery which cannot start is observably queued, so a test that merely
/// waited long enough would be asserting on the scheduler, not the bound.
/// </remarks>
public sealed class WebhookInboundQueueTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly AgentId Target = AgentId.From("tinker");
    private static readonly ConversationId Conversation = ConversationId.From("conv-3851");

    private static WebhookInboundQueue CreateQueue(int depth = 2, TimeSpan? runTimeout = null) =>
        new(new WebhookInboundQueueOptions
        {
            MaxQueueDepth = depth,
            RunTimeout = runTimeout ?? WebhookInboundQueueOptions.DefaultRunTimeout
        });

    [Fact]
    public async Task FirstDelivery_IsAdmittedImmediately_AndNeverReportsQueued()
    {
        var queue = CreateQueue();

        var ticket = queue.Admit(Target, Conversation);

        ticket.IsImmediate.ShouldBeTrue(
            "an uncontended delivery must not be reported as having waited");
        queue.WaitingCount(Target).ShouldBe(0);

        using var lease = await ticket.WaitAsync(CancellationToken.None);
        lease.ShouldNotBeNull();
    }

    [Fact]
    public async Task SecondDelivery_WhileSlotHeld_IsQueuedNotRunning()
    {
        // AC2: a delivery blocked before acquiring the agent's slot must be distinguishable from
        // one actually executing. Pre-fix both reported Running.
        var queue = CreateQueue();
        var holder = await queue.Admit(Target, Conversation).WaitAsync(CancellationToken.None);

        var second = queue.Admit(Target, Conversation);

        second.IsImmediate.ShouldBeFalse("the slot is held, so this delivery genuinely waits");
        queue.WaitingCount(Target).ShouldBe(1);

        var waiting = second.WaitAsync(CancellationToken.None);
        waiting.IsCompleted.ShouldBeFalse("the waiter cannot proceed while the slot is held");

        holder.Dispose();
        using var acquired = await waiting.WaitAsync(TestTimeout);
        queue.WaitingCount(Target).ShouldBe(0);
    }

    [Fact]
    public async Task DeliveriesBeyondTheBound_AreRejectedWithBackpressure()
    {
        // AC4: the caller is refused explicitly rather than handed a 202 that may never be serviced.
        var queue = CreateQueue(depth: 2);
        using var holder = await queue.Admit(Target, Conversation).WaitAsync(CancellationToken.None);

        var first = queue.Admit(Target, Conversation);
        var second = queue.Admit(Target, Conversation);
        _ = first.WaitAsync(CancellationToken.None);
        _ = second.WaitAsync(CancellationToken.None);

        queue.WaitingCount(Target).ShouldBe(2);

        var refusal = Should.Throw<WebhookBackpressureException>(() => queue.Admit(Target, Conversation));
        refusal.TargetId.ShouldBe(Target);
        refusal.MaxQueueDepth.ShouldBe(2);
        refusal.ShouldNotBeAssignableTo<OperationCanceledException>(
            "backpressure must be distinguishable from a deadline expiring");
    }

    [Fact]
    public async Task QueuedDelivery_WhoseDeadlineExpires_ReportsNotDispatched()
    {
        // AC3: the wait honours a real cancellation token, and the resulting signal says the agent
        // never saw the message rather than collapsing into a bare cancellation.
        var queue = CreateQueue();
        using var holder = await queue.Admit(Target, Conversation).WaitAsync(CancellationToken.None);

        var ticket = queue.Admit(Target, Conversation);
        using var cts = new CancellationTokenSource();
        var waiting = ticket.WaitAsync(cts.Token);

        await cts.CancelAsync();

        var undispatched = await Should.ThrowAsync<WebhookNotDispatchedException>(
            async () => await waiting.WaitAsync(TestTimeout));
        undispatched.TargetId.ShouldBe(Target);
        undispatched.ShouldNotBeAssignableTo<OperationCanceledException>();

        // AC4 corollary: an abandoned waiter returns its depth, or the bound would shrink until the
        // queue wedged permanently shut.
        queue.WaitingCount(Target).ShouldBe(0);
    }

    [Fact]
    public async Task BacklogDepth_IsObservableThroughTheDepthEvent()
    {
        // AC5: a growing backlog is diagnosable without reading run rows.
        var queue = CreateQueue(depth: 4);
        var observed = new List<int>();
        var sync = new object();
        queue.WaitingCountChanged += (_, waiting) => { lock (sync) { observed.Add(waiting); } };

        var holder = await queue.Admit(Target, Conversation).WaitAsync(CancellationToken.None);
        var first = queue.Admit(Target, Conversation);
        var second = queue.Admit(Target, Conversation);
        _ = first.WaitAsync(CancellationToken.None);
        var secondWait = second.WaitAsync(CancellationToken.None);

        lock (sync)
        {
            observed.ShouldContain(1, "the first waiter must announce a depth of 1");
            observed.ShouldContain(2, "the second waiter must announce a depth of 2");
        }

        holder.Dispose();
        await secondWait.WaitAsync(TestTimeout).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    [Fact]
    public async Task DistinctConversations_DoNotShareASlot()
    {
        // Mutual exclusion is keyed on the CONVERSATION, per #2123: distinct conversations are the
        // sanctioned route to real parallelism, and this queue must not revoke it.
        var queue = CreateQueue(depth: 1);
        using var holder = await queue.Admit(Target, Conversation).WaitAsync(CancellationToken.None);

        var other = queue.Admit(Target, ConversationId.From("conv-other"));

        other.IsImmediate.ShouldBeTrue(
            "a delivery to a different conversation must not serialize behind this one");
        queue.WaitingCount(Target).ShouldBe(0);
    }

    [Fact]
    public async Task TheBoundIsPerAgent_AcrossAllOfItsConversations()
    {
        // Saturation is an agent-level phenomenon - what overloads is the agent being addressed
        // faster than it can answer, no matter how many conversations the traffic arrives on.
        var queue = CreateQueue(depth: 1);
        using var firstHolder = await queue.Admit(Target, Conversation).WaitAsync(CancellationToken.None);
        var otherConversation = ConversationId.From("conv-other");
        using var secondHolder = await queue.Admit(Target, otherConversation).WaitAsync(CancellationToken.None);

        var waiter = queue.Admit(Target, Conversation);
        _ = waiter.WaitAsync(CancellationToken.None);
        queue.WaitingCount(Target).ShouldBe(1);

        // The bound is consumed by a waiter on a DIFFERENT conversation, and still applies here.
        Should.Throw<WebhookBackpressureException>(() => queue.Admit(Target, otherConversation));
    }

    [Fact]
    public async Task DistinctAgents_DoNotShareABound()
    {
        var queue = CreateQueue(depth: 1);
        using var holder = await queue.Admit(Target, Conversation).WaitAsync(CancellationToken.None);
        var waiter = queue.Admit(Target, Conversation);
        _ = waiter.WaitAsync(CancellationToken.None);

        var otherAgent = AgentId.From("aurum");
        queue.WaitingCount(Target).ShouldBe(1);
        queue.WaitingCount(otherAgent).ShouldBe(0, "one busy agent must not consume another's bound");

        queue.Admit(otherAgent, ConversationId.From("conv-aurum")).IsImmediate.ShouldBeTrue();
    }

    [Fact]
    public async Task Lease_ReleasesTheSlotExactlyOnce()
    {
        // A double dispose must not admit two deliveries at a time.
        var queue = CreateQueue();
        var lease = await queue.Admit(Target, Conversation).WaitAsync(CancellationToken.None);

        lease.Dispose();
        lease.Dispose();

        var next = queue.Admit(Target, Conversation);
        next.IsImmediate.ShouldBeTrue();

        // If the double dispose had over-released, a THIRD admission would also be immediate while
        // the second still holds the slot.
        _ = next.WaitAsync(CancellationToken.None);
        queue.Admit(Target, Conversation).IsImmediate.ShouldBeFalse(
            "a double-disposed lease must not have released the slot twice");
    }

    [Fact]
    public async Task AbandonedQueuedTicket_ReturnsCapacity_WithoutDispatching()
    {
        var queue = CreateQueue(depth: 1);
        using var holder = await queue.Admit(Target, Conversation).WaitAsync(CancellationToken.None);
        var abandoned = queue.Admit(Target, Conversation);

        abandoned.Dispose();

        queue.WaitingCount(Target).ShouldBe(0);
        var replacement = queue.Admit(Target, Conversation);
        replacement.IsImmediate.ShouldBeFalse();
        replacement.Dispose();
    }

    [Fact]
    public async Task Abandonment_IsIdempotent_AndCannotReleaseAnotherLease()
    {
        var queue = CreateQueue(depth: 1);
        using var holder = await queue.Admit(Target, Conversation).WaitAsync(CancellationToken.None);
        var abandoned = queue.Admit(Target, Conversation);

        abandoned.Dispose();
        abandoned.Dispose();

        queue.WaitingCount(Target).ShouldBe(0);
        var replacement = queue.Admit(Target, Conversation);
        replacement.IsImmediate.ShouldBeFalse(
            "abandoning a waiter must not release the current holder's semaphore lease");
        replacement.Dispose();
    }

    [Fact]
    public async Task CancellationRacingAbandonment_ReleasesReservationExactlyOnce()
    {
        var queue = CreateQueue(depth: 1);
        using var holder = await queue.Admit(Target, Conversation).WaitAsync(CancellationToken.None);
        var ticket = queue.Admit(Target, Conversation);
        using var cts = new CancellationTokenSource();

        var wait = Task.Run(async () =>
        {
            try { using var lease = await ticket.WaitAsync(cts.Token); }
            catch (WebhookNotDispatchedException) { }
            catch (InvalidOperationException) { }
        });
        await Task.WhenAll(Task.Run(ticket.Dispose), cts.CancelAsync()).WaitAsync(TestTimeout);
        await wait.WaitAsync(TestTimeout);

        queue.WaitingCount(Target).ShouldBe(0);
        var replacement = queue.Admit(Target, Conversation);
        replacement.IsImmediate.ShouldBeFalse(
            "the abandonment race must not release the current holder's semaphore lease");
        replacement.Dispose();
    }

    [Fact]
    public async Task AbandonedImmediateTicket_ReleasesItsLeaseExactlyOnce()
    {
        var queue = CreateQueue();
        var ticket = queue.Admit(Target, Conversation);

        ticket.Dispose();
        ticket.Dispose();

        var replacement = queue.Admit(Target, Conversation);
        replacement.IsImmediate.ShouldBeTrue();
        using var lease = await replacement.WaitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Ticket_CannotBeConsumedTwice()
    {
        var queue = CreateQueue();
        var ticket = queue.Admit(Target, Conversation);
        using var lease = await ticket.WaitAsync(CancellationToken.None);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await ticket.WaitAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveDepth_FallsBackToOne_RatherThanDisablingDelivery(int configured)
    {
        var options = new WebhookInboundQueueOptions { MaxQueueDepth = configured };
        options.EffectiveMaxQueueDepth.ShouldBe(1);
    }

    [Fact]
    public void NonPositiveRunTimeout_FallsBackToTheDefault_SoNoTimeoutIsUnreachable()
    {
        // "No timeout" is the defect this option closes, so misconfiguration must not restore it.
        var options = new WebhookInboundQueueOptions { RunTimeout = TimeSpan.Zero };
        options.EffectiveRunTimeout.ShouldBe(WebhookInboundQueueOptions.DefaultRunTimeout);

        var negative = new WebhookInboundQueueOptions { RunTimeout = TimeSpan.FromSeconds(-1) };
        negative.EffectiveRunTimeout.ShouldBe(WebhookInboundQueueOptions.DefaultRunTimeout);

        var unsupported = new WebhookInboundQueueOptions { RunTimeout = TimeSpan.MaxValue };
        unsupported.EffectiveRunTimeout.ShouldBe(WebhookInboundQueueOptions.DefaultRunTimeout);
    }

    [Fact]
    public async Task AdmissionOrderWins_WhenLaterTicketStartsWaitingFirst()
    {
        var queue = CreateQueue(depth: 2);
        var holder = await queue.Admit(Target, Conversation).WaitAsync(CancellationToken.None);
        var firstTicket = queue.Admit(Target, Conversation);
        var secondTicket = queue.Admit(Target, Conversation);

        var secondWait = secondTicket.WaitAsync(CancellationToken.None);
        var firstWait = firstTicket.WaitAsync(CancellationToken.None);

        holder.Dispose();
        var winner = await Task.WhenAny(firstWait, secondWait).WaitAsync(TestTimeout);

        winner.ShouldBe(firstWait,
            "execution ownership must follow admission order, not the order callers reach WaitAsync");
        var firstLease = await firstWait.WaitAsync(TestTimeout);
        secondWait.IsCompleted.ShouldBeFalse("only the first admitted ticket receives this handoff");
        firstLease.Dispose();
        (await secondWait.WaitAsync(TestTimeout)).Dispose();
    }

    [Fact]
    public async Task CancellingFirstAdmittedTicket_RemovesItAndAdvancesSuccessor()
    {
        var queue = CreateQueue(depth: 2);
        var holder = await queue.Admit(Target, Conversation).WaitAsync(CancellationToken.None);
        var cancelledTicket = queue.Admit(Target, Conversation);
        var successorTicket = queue.Admit(Target, Conversation);
        using var cts = new CancellationTokenSource();

        var cancelledWait = cancelledTicket.WaitAsync(cts.Token);
        var successorWait = successorTicket.WaitAsync(CancellationToken.None);
        await cts.CancelAsync();

        await Should.ThrowAsync<WebhookNotDispatchedException>(
            async () => await cancelledWait.WaitAsync(TestTimeout));
        holder.Dispose();
        using var successorLease = await successorWait.WaitAsync(TestTimeout);
        queue.WaitingCount(Target).ShouldBe(0);
    }

    [Fact]
    public async Task CompletedDeliveries_RetireIdleConversationAndAgentBookkeeping()
    {
        var queue = CreateQueue(depth: 2);

        for (var index = 0; index < 100; index++)
        {
            var conversation = ConversationId.From($"conv-retired-{index}");
            var holder = await queue.Admit(Target, conversation).WaitAsync(CancellationToken.None);
            var queued = queue.Admit(Target, conversation);
            var queuedWait = queued.WaitAsync(CancellationToken.None);

            holder.Dispose();
            (await queuedWait.WaitAsync(TestTimeout)).Dispose();
        }

        queue.RetainedSlotCount.ShouldBe(0,
            "completed conversations must not remain in the singleton queue indefinitely");
        queue.RetainedAgentDepthCount.ShouldBe(0,
            "agents with no queued deliveries must not retain depth bookkeeping indefinitely");
    }

    [Fact]
    public async Task RetirementRacingAdmission_PreservesSingleConversationHolder()
    {
        var queue = CreateQueue(depth: 2);
        var holder = await queue.Admit(Target, Conversation).WaitAsync(CancellationToken.None);
        using var slotResolved = new ManualResetEventSlim();
        using var continueAdmission = new ManualResetEventSlim();
        var pauseOnce = 0;
        queue.SlotResolvedForTesting = () =>
        {
            if (Interlocked.Exchange(ref pauseOnce, 1) != 0)
                return;
            slotResolved.Set();
            continueAdmission.Wait(TestTimeout);
        };

        var contenderTask = Task.Run(() => queue.Admit(Target, Conversation));
        slotResolved.Wait(TestTimeout).ShouldBeTrue(
            "the contender must resolve the retiring slot before the holder releases it");
        holder.Dispose();
        queue.RetainedSlotCount.ShouldBe(0,
            "the idle slot must retire while the contender still holds its stale reference");
        continueAdmission.Set();

        var contender = await contenderTask.WaitAsync(TestTimeout);
        using var contenderLease = await contender.WaitAsync(CancellationToken.None).WaitAsync(TestTimeout);
        var follower = queue.Admit(Target, Conversation);
        follower.IsImmediate.ShouldBeFalse(
            "the stale contender must retry against the replacement slot rather than bypass it");
        follower.Dispose();

        queue.SlotResolvedForTesting = null;
        contenderLease.Dispose();
        queue.RetainedSlotCount.ShouldBe(0);
        queue.RetainedAgentDepthCount.ShouldBe(0);
    }

    [Fact]
    public async Task CancelledRejectedAndAbandonedAdmissions_DoNotRetainBookkeeping()
    {
        var queue = CreateQueue(depth: 1);
        var holder = await queue.Admit(Target, Conversation).WaitAsync(CancellationToken.None);

        var cancelled = queue.Admit(Target, Conversation);
        using var cts = new CancellationTokenSource();
        var cancelledWait = cancelled.WaitAsync(cts.Token);
        Should.Throw<WebhookBackpressureException>(() => queue.Admit(Target, Conversation));
        await cts.CancelAsync();
        await Should.ThrowAsync<WebhookNotDispatchedException>(
            async () => await cancelledWait.WaitAsync(TestTimeout));

        var abandoned = queue.Admit(Target, Conversation);
        abandoned.Dispose();
        holder.Dispose();

        queue.RetainedSlotCount.ShouldBe(0);
        queue.RetainedAgentDepthCount.ShouldBe(0);
    }

    [Fact]
    public async Task WaitersAreServedInFifoOrder_WithNoBarging()
    {
        var queue = CreateQueue(depth: 8);
        var holder = await queue.Admit(Target, Conversation).WaitAsync(CancellationToken.None);

        var firstWaiter = queue.Admit(Target, Conversation);
        var secondWaiter = queue.Admit(Target, Conversation);
        var firstWait = firstWaiter.WaitAsync(CancellationToken.None);
        var secondWait = secondWaiter.WaitAsync(CancellationToken.None);

        // A delivery arriving now must not be admitted ahead of the two already waiting.
        var latecomer = queue.Admit(Target, Conversation);
        latecomer.IsImmediate.ShouldBeFalse(
            "a newly arriving delivery must not barge ahead of queued ones");

        holder.Dispose();
        var firstLease = await firstWait.WaitAsync(TestTimeout);
        secondWait.IsCompleted.ShouldBeFalse("only one waiter is released per handoff");
        firstLease.Dispose();
        (await secondWait.WaitAsync(TestTimeout)).Dispose();
    }
}

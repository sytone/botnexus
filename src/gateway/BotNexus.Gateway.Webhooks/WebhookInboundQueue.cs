using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Per-target-agent admission control for inbound webhook deliveries (#3851).
/// </summary>
/// <remarks>
/// <para>
/// This is the webhook-path port of <c>AgentExchangeInboundQueue</c>, which closed the identical
/// defect on the <c>agent_converse</c> entry point in #3494. The shape is deliberately the same,
/// because the underlying constraint is the same: an in-process agent has exactly one execution
/// slot, and an entry point that does not model that slot does not eliminate the queue - it only
/// makes the queue invisible, unbounded and undiagnosable.
/// </para>
/// <para>
/// Before this type existed the webhook controller dispatched every delivery with an unbounded
/// fire-and-forget <c>Task.Run</c>. Deliveries to a busy agent piled up behind the per-session write
/// lock with no depth bound and no deadline, while every one of them reported
/// <see cref="WebhookRunStatus.Running"/> - so a run blocked on a mutex was indistinguishable from a
/// run actually executing.
/// </para>
/// <para>
/// Admission is deliberately separated from waiting. <see cref="Admit"/> is synchronous and decides
/// <em>now</em>, on the request thread, whether this delivery is accepted at all; only the
/// subsequent <see cref="WebhookQueueTicket.WaitAsync"/> blocks. That split is what lets the caller
/// receive an honest refusal instead of a <c>202 Accepted</c> for work that may never be serviced,
/// which is the whole of AC4. An unbounded mailbox does not avoid failure - it converts an explicit
/// rejection into unbounded latency, which is worse because the caller has already been told yes.
/// </para>
/// <para>
/// Mutual exclusion is keyed on the CONVERSATION and the bound on the AGENT. #2123 made the
/// conversation the gateway's isolation unit, so serialising an agent's distinct conversations here
/// would silently revoke sanctioned parallelism; but saturation is an agent-level phenomenon, so
/// that is where the depth bound and the depth signal belong.
/// </para>
/// </remarks>
public sealed class WebhookInboundQueue
{
    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentDepth> _depths = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptions<WebhookInboundQueueOptions> _options;

    /// <summary>Creates a queue bound by <paramref name="options"/>.</summary>
    public WebhookInboundQueue(IOptions<WebhookInboundQueueOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Creates a queue with explicit bounds, for tests and non-DI callers.</summary>
    public WebhookInboundQueue(WebhookInboundQueueOptions options)
        : this(Options.Create(options ?? throw new ArgumentNullException(nameof(options))))
    {
    }

    /// <summary>
    /// Raised whenever the number of deliveries waiting for an agent's slot changes, with the target
    /// agent and the new waiting count.
    /// </summary>
    /// <remarks>
    /// Backlog depth is the signal that says "this agent is being addressed faster than it can
    /// answer" (AC5), and it is invisible from outside: by the time backpressure is thrown the queue
    /// is already full. Handlers run inline on the admission path, so they must be cheap; a throwing
    /// handler is swallowed rather than allowed to fail a delivery that was otherwise fine.
    /// </remarks>
    public event Action<AgentId, int>? WaitingCountChanged;

    /// <summary>
    /// Deliveries currently WAITING anywhere for <paramref name="targetId"/>. The in-flight holders
    /// are excluded, so an uncontended delivery never counts against the bound.
    /// </summary>
    public int WaitingCount(AgentId targetId)
        => _depths.TryGetValue(targetId.Value, out var depth) ? Volatile.Read(ref depth.Waiting) : 0;

    /// <summary>The configured bound, guaranteed to be at least 1.</summary>
    public int MaxQueueDepth => _options.Value.EffectiveMaxQueueDepth;

    /// <summary>The configured per-run ceiling, guaranteed to be positive.</summary>
    public TimeSpan RunTimeout => _options.Value.EffectiveRunTimeout;

    /// <summary>
    /// Decides synchronously whether this delivery is admitted, taking the conversation's execution
    /// slot outright when it is free and nobody is queued ahead, or reserving a place in the
    /// agent's bounded queue otherwise.
    /// </summary>
    /// <param name="targetId">The agent the delivery is addressed to; the unit the BOUND applies to.</param>
    /// <param name="conversationId">
    /// The conversation the delivery routes to; the unit MUTUAL EXCLUSION applies to.
    /// </param>
    /// <remarks>
    /// The two units are deliberately different. #2123 established the conversation as the gateway's
    /// isolation unit - deliveries to distinct conversations are the sanctioned route to real
    /// parallelism, and serialising them on the agent would silently revoke that. But the backlog
    /// that #3851 reports is an agent-level phenomenon: what saturates is the agent being addressed
    /// faster than it can answer, across all of its conversations at once. So the slot is per
    /// conversation and the bound and depth signal are per agent.
    /// </remarks>
    /// <returns>
    /// A ticket that is either already holding the slot
    /// (<see cref="WebhookQueueTicket.IsImmediate"/>) or must be awaited via
    /// <see cref="WebhookQueueTicket.WaitAsync"/>. The caller must dispose the lease it yields.
    /// </returns>
    /// <exception cref="WebhookBackpressureException">
    /// The bound is already fully subscribed. An explicit refusal, never a silent drop.
    /// </exception>
    public WebhookQueueTicket Admit(AgentId targetId, ConversationId conversationId)
    {
        var slot = _slots.GetOrAdd(conversationId.Value, static _ => new Slot());
        var depth = _depths.GetOrAdd(targetId.Value, static _ => new AgentDepth());
        int waitingAfterAdmission;

        // Fast path: free slot AND nobody queued ahead of us on THIS conversation. The waiter check
        // is what keeps this FIFO - without it a newly arriving delivery could snatch the slot the
        // instant the holder released it, ahead of a delivery that has been waiting. Barging is how
        // a queued delivery starves, which is the same silent loss in slower clothing.
        lock (slot.SyncRoot)
        {
            if (slot.Waiting == 0 && slot.Gate.Wait(0, CancellationToken.None))
                return new WebhookQueueTicket(this, slot, depth, targetId, isImmediate: true);

            var maxDepth = MaxQueueDepth;
            lock (depth.SyncRoot)
            {
                if (depth.Waiting >= maxDepth)
                    throw new WebhookBackpressureException(targetId, maxDepth);
                waitingAfterAdmission = ++depth.Waiting;
            }

            slot.Waiting++;
        }

        RaiseWaitingCountChanged(targetId, waitingAfterAdmission);
        return new WebhookQueueTicket(this, slot, depth, targetId, isImmediate: false);
    }

    /// <summary>
    /// Invokes <see cref="WaitingCountChanged"/>, absorbing any handler failure - observability must
    /// never be the thing that breaks the delivery it is observing.
    /// </summary>
    internal void RaiseWaitingCountChanged(AgentId targetId, int waiting)
    {
        try
        {
            WaitingCountChanged?.Invoke(targetId, waiting);
        }
        catch
        {
            // Intentionally swallowed - see summary.
        }
    }

    internal sealed class Slot
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly object SyncRoot = new();
        public int Waiting;
    }

    /// <summary>Per-agent backlog counter: the unit the bound and the depth signal apply to.</summary>
    internal sealed class AgentDepth
    {
        public readonly object SyncRoot = new();
        public int Waiting;
    }

    /// <summary>
    /// Releases the conversation slot exactly once, so a double-dispose cannot admit two deliveries
    /// at a time.
    /// </summary>
    internal sealed class Lease(Slot slot, WebhookInboundQueue owner, AgentId targetId) : IDisposable
    {
        private Slot? _slot = slot;

        public void Dispose()
        {
            var released = Interlocked.Exchange(ref _slot, null);
            if (released is null)
                return;
            released.Gate.Release();
            // Re-announce depth after the handoff so an observer sees the queue drain, not only
            // fill. Without this the last transition an observer sees is the peak.
            owner.RaiseWaitingCountChanged(targetId, owner.WaitingCount(targetId));
        }
    }
}

/// <summary>
/// The outcome of <see cref="WebhookInboundQueue.Admit"/>: an accepted delivery that either holds
/// the target agent's execution slot already or has a reserved place in the bounded queue.
/// </summary>
public sealed class WebhookQueueTicket
{
    private readonly WebhookInboundQueue _owner;
    private readonly WebhookInboundQueue.Slot _slot;
    private readonly WebhookInboundQueue.AgentDepth _depth;
    private readonly AgentId _targetId;
    private int _consumed;

    internal WebhookQueueTicket(
        WebhookInboundQueue owner,
        WebhookInboundQueue.Slot slot,
        WebhookInboundQueue.AgentDepth depth,
        AgentId targetId,
        bool isImmediate)
    {
        _owner = owner;
        _slot = slot;
        _depth = depth;
        _targetId = targetId;
        IsImmediate = isImmediate;
    }

    /// <summary>
    /// <see langword="true"/> when the slot was free and this delivery took it without waiting.
    /// </summary>
    /// <remarks>
    /// The controller reports <see cref="WebhookRunStatus.Queued"/> only when this is
    /// <see langword="false"/>. A run that was admitted straight through never waited, and must not
    /// be recorded as having done so - a queued state that is always set is no more informative than
    /// the <c>Running</c>-for-everything it replaces.
    /// </remarks>
    public bool IsImmediate { get; }

    /// <summary>
    /// Waits for the target agent's execution slot and yields the lease that holds it.
    /// </summary>
    /// <param name="cancellationToken">Fires on run timeout or host shutdown.</param>
    /// <exception cref="WebhookNotDispatchedException">
    /// The token fired while this delivery was still queued, so the agent never saw the message.
    /// </exception>
    public async Task<IDisposable> WaitAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _consumed, 1) == 1)
            throw new InvalidOperationException("This webhook queue ticket has already been consumed.");

        if (IsImmediate)
            return new WebhookInboundQueue.Lease(_slot, _owner, _targetId);

        try
        {
            await _slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new WebhookNotDispatchedException(_targetId);
        }
        finally
        {
            // Decrement on EVERY exit, including the cancelled one: a waiter that abandons without
            // returning its depth would permanently shrink the bound until the queue wedged shut.
            int waitingAfterExit;
            lock (_slot.SyncRoot)
                _slot.Waiting--;
            lock (_depth.SyncRoot)
                waitingAfterExit = --_depth.Waiting;
            _owner.RaiseWaitingCountChanged(_targetId, waitingAfterExit);
        }

        return new WebhookInboundQueue.Lease(_slot, _owner, _targetId);
    }
}

/// <summary>
/// Thrown when a target agent's bounded inbound webhook queue is full (#3851 AC4).
/// </summary>
/// <remarks>
/// Deliberately NOT an <see cref="OperationCanceledException"/>: the caller must be able to tell
/// "this agent is saturated, back off and retry" from "my deadline expired". The controller maps it
/// to <c>503 Service Unavailable</c> so the delivery is explicitly refused rather than accepted with
/// a <c>202</c> that may never be serviced.
/// </remarks>
public sealed class WebhookBackpressureException : Exception
{
    /// <summary>Creates the refusal for a saturated agent.</summary>
    public WebhookBackpressureException(AgentId targetId, int maxQueueDepth)
        : base($"Agent '{targetId.Value}' is busy and its inbound webhook queue is full "
               + $"({maxQueueDepth} waiting). The delivery was NOT accepted and no agent run was started. "
               + "Retry later, or reduce the rate of webhook deliveries to this agent.")
    {
        TargetId = targetId;
        MaxQueueDepth = maxQueueDepth;
    }

    /// <summary>The saturated agent.</summary>
    public AgentId TargetId { get; }

    /// <summary>The bound that was exceeded.</summary>
    public int MaxQueueDepth { get; }
}

/// <summary>
/// Thrown when the run deadline elapsed while the delivery was still queued (#3851 AC3).
/// </summary>
/// <remarks>
/// Deliberately NOT an <see cref="OperationCanceledException"/>, for the same reason as its
/// <c>agent_converse</c> counterpart: the async machinery rewrites a cancellation-derived exception
/// into a plain <see cref="TaskCanceledException"/> when the task transitions to <c>Canceled</c>,
/// destroying the type before any caller can inspect it - so a cancellation-derived "this was not a
/// plain cancellation" signal cannot survive its own await.
/// </remarks>
public sealed class WebhookNotDispatchedException : Exception
{
    /// <summary>Creates the undispatched signal for a delivery that never reached the agent.</summary>
    public WebhookNotDispatchedException(AgentId targetId)
        : base($"The webhook delivery to agent '{targetId.Value}' was never dispatched: the run deadline "
               + "elapsed while it was still queued behind that agent's in-flight work. "
               + "The agent never received the message, so retrying is safe.")
    {
        TargetId = targetId;
    }

    /// <summary>The agent the undispatched delivery was addressed to.</summary>
    public AgentId TargetId { get; }
}

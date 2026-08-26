using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Per-target-agent admission control for inbound agent-to-agent exchanges (#3494).
/// </summary>
/// <remarks>
/// <para>
/// An in-process agent has exactly one execution slot: <c>IAgentHandle.PromptAsync</c> is a
/// blocking turn on a single agent loop. Before this type existed, <c>agent_converse</c> had no
/// notion of that slot at all. A second inbound exchange arriving while the target was busy (or
/// parked in a long <c>delay()</c>) was neither queued nor refused - it minted an exchange
/// session, blocked somewhere downstream, and was eventually killed by the CALLER's own bounded
/// timeout. The user saw a bare <c>task was canceled</c> and the store kept a one-row
/// <c>Active</c> session forever.
/// </para>
/// <para>
/// This is a FIFO mailbox with a bounded number of waiters, not an unbounded work queue.
/// Boundedness is the point: an unbounded mailbox converts message loss into unbounded latency,
/// which is a worse failure because it is invisible. When the bound is exceeded the caller gets
/// <see cref="AgentExchangeBackpressureException"/> - a refusal it can retry or report - and when
/// a caller gives up while still waiting it gets <see cref="AgentExchangeNotDispatchedException"/>,
/// which says the message never reached the agent at all.
/// </para>
/// <para>
/// Neither exception derives from <see cref="OperationCanceledException"/>, deliberately. The
/// whole defect was that every distinct outcome collapsed into one indistinguishable
/// cancellation; a backpressure signal that is itself a cancellation would reintroduce it.
/// </para>
/// </remarks>
public sealed class AgentExchangeInboundQueue
{
    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptions<AgentExchangeOptions> _options;

    public AgentExchangeInboundQueue(IOptions<AgentExchangeOptions> options)
    {
        _options = options;
    }

    /// <summary>
    /// Raised whenever the number of exchanges waiting for an agent's slot changes, with the
    /// target agent and the new waiting count.
    /// </summary>
    /// <remarks>
    /// Mailbox depth is the one signal that says "this agent is being addressed faster than it can
    /// answer", and it is invisible from outside: by the time backpressure is thrown, the queue has
    /// already been full. Exposing the transition lets a diagnostic surface observe saturation
    /// building rather than only its overflow. Handlers are invoked inline, so they must be cheap
    /// and must not throw - a throwing observer would fail an exchange that was otherwise fine.
    /// </remarks>
    public event Action<AgentId, int>? WaitingCountChanged;

    /// <summary>
    /// The number of exchanges currently WAITING for the target's slot. The in-flight holder is
    /// excluded: only genuinely blocked callers consume queue depth, so an uncontended exchange
    /// never counts against the bound.
    /// </summary>
    public int WaitingCount(AgentId targetId)
        => _slots.TryGetValue(targetId.Value, out var slot) ? Volatile.Read(ref slot.Waiting) : 0;

    /// <summary>
    /// Acquires the target agent's single execution slot, waiting in FIFO order when it is busy.
    /// </summary>
    /// <exception cref="AgentExchangeBackpressureException">
    /// The mailbox already holds <see cref="AgentExchangeOptions.EffectiveMaxInboundQueueDepth"/>
    /// waiters. Explicit refusal, never a silent drop.
    /// </exception>
    /// <exception cref="AgentExchangeNotDispatchedException">
    /// <paramref name="cancellationToken"/> fired while this exchange was still queued, so the
    /// target agent never saw the message.
    /// </exception>
    public async Task<IDisposable> AcquireAsync(AgentId targetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var slot = _slots.GetOrAdd(targetId.Value, static _ => new Slot());
        int waitingAfterAdmission;

        // Fast path: free slot AND nobody queued ahead of us. The waiter check is what makes this
        // safe - without it a freshly-arriving exchange could snatch the slot the instant the
        // holder released it, ahead of a caller that has been waiting, and FIFO would degrade into
        // "whoever happens to arrive at the right microsecond". Barging is how a queued exchange
        // starves, which is the same message loss in slower clothing.
        lock (slot.SyncRoot)
        {
            if (slot.Waiting == 0 && slot.Gate.Wait(0, CancellationToken.None))
                return new Lease(slot, this, targetId);

            var maxDepth = _options.Value.EffectiveMaxInboundQueueDepth;
            if (slot.Waiting >= maxDepth)
                throw new AgentExchangeBackpressureException(targetId, maxDepth);
            waitingAfterAdmission = ++slot.Waiting;
        }

        RaiseWaitingCountChanged(targetId, waitingAfterAdmission);

        try
        {
            await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The message never reached the agent. Say so, rather than letting a bare
            // OperationCanceledException surface as the "task was canceled" this issue is about.
            throw new AgentExchangeNotDispatchedException(targetId);
        }
        finally
        {
            // Decrement on EVERY exit, including the cancelled one: a waiter that abandons without
            // releasing its depth would permanently shrink the mailbox until it wedged shut.
            int waitingAfterExit;
            lock (slot.SyncRoot)
                waitingAfterExit = --slot.Waiting;
            RaiseWaitingCountChanged(targetId, waitingAfterExit);
        }

        return new Lease(slot, this, targetId);
    }

    /// <summary>
    /// Invokes <see cref="WaitingCountChanged"/>, absorbing any handler failure.
    /// </summary>
    /// <remarks>
    /// Observability must never be the thing that breaks the exchange it is observing, and this
    /// runs on the admission path of every contended call.
    /// </remarks>
    private void RaiseWaitingCountChanged(AgentId targetId, int waiting)
    {
        try
        {
            WaitingCountChanged?.Invoke(targetId, waiting);
        }
        catch
        {
            // Intentionally swallowed - see remarks.
        }
    }

    private sealed class Slot
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly object SyncRoot = new();
        public int Waiting;
    }

    /// <summary>
    /// Releases the slot exactly once, so a double-dispose cannot admit two exchanges at a time.
    /// </summary>
    private sealed class Lease(Slot slot, AgentExchangeInboundQueue owner, AgentId targetId) : IDisposable
    {
        private Slot? _slot = slot;

        public void Dispose()
        {
            var released = Interlocked.Exchange(ref _slot, null);
            if (released is null)
                return;
            released.Gate.Release();
            // Re-announce the depth after the handoff so an observer sees the queue drain, not
            // only fill. Without this the last transition an observer sees is the peak.
            int waiting;
            lock (released.SyncRoot)
                waiting = released.Waiting;
            owner.RaiseWaitingCountChanged(targetId, waiting);
        }
    }
}

/// <summary>
/// Thrown when a target agent's bounded inbound mailbox is full (#3494 AC2).
/// </summary>
/// <remarks>
/// Deliberately NOT an <see cref="OperationCanceledException"/>: the caller must be able to tell
/// "the target is saturated, back off and retry" from "my deadline expired".
/// </remarks>
public sealed class AgentExchangeBackpressureException : Exception
{
    public AgentExchangeBackpressureException(AgentId targetId, int maxQueueDepth)
        : base($"Agent '{targetId.Value}' is busy and its inbound exchange queue is full "
               + $"({maxQueueDepth} waiting). The message was NOT delivered and no exchange was started. "
               + "Retry later, or reduce the rate of concurrent agent_converse calls to this agent.")
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
/// Thrown when the caller's deadline elapsed while the exchange was still queued (#3494 AC3).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately NOT an <see cref="OperationCanceledException"/>, for two reasons that point the
/// same way. Semantically, every generic <c>catch (OperationCanceledException)</c> between here
/// and the user is a place this distinction would be erased - and erasing it is the entire defect
/// in #3494, where four separate stranded exchanges all surfaced as one indistinguishable
/// <c>task was canceled</c>. Mechanically, the async machinery transitions a task whose exception
/// derives from <see cref="OperationCanceledException"/> into the <c>Canceled</c> state, which
/// replaces the awaited exception with a plain <see cref="TaskCanceledException"/> and destroys
/// the type before any caller can inspect it. A cancellation-derived "this was not a plain
/// cancellation" signal cannot survive its own await.
/// </para>
/// </remarks>
public sealed class AgentExchangeNotDispatchedException : Exception
{
    public AgentExchangeNotDispatchedException(AgentId targetId)
        : base($"The exchange with agent '{targetId.Value}' was never dispatched: the caller's deadline "
               + "elapsed while the message was still queued behind that agent's in-flight work. "
               + "The target agent never received the message, so retrying is safe.")
    {
        TargetId = targetId;
    }

    /// <summary>The agent the undispatched exchange was addressed to.</summary>
    public AgentId TargetId { get; }
}

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Delivery semantics a transport requests for an inbound message (#3028). This states the
/// caller's <em>intent</em>; the gateway resolves that intent to a mechanism server-side.
/// </summary>
/// <remarks>
/// <para>
/// Before #3028 the "steer or queue?" question was answered by each caller, and for the desktop
/// portal it was answered in a Razor component reading client-side stream state. That made
/// queue-only semantics the silent default for every non-hub surface (CLI, webhook, the
/// <c>POST /api/agents/{agentId}/conversations/{conversationId}/messages</c> endpoint) and let two
/// clients diverge on the same user action. A caller now states one of these values and the
/// inbound seam decides, on evidence the server owns (is a turn actually running?), which
/// mechanism implements it.
/// </para>
/// <para>
/// <b>The default is <see cref="Auto"/>, and <see cref="Auto"/> queues.</b> Steering injects into a
/// turn already in flight, which has ordering and context-window consequences a queued message does
/// not; making that automatic for any busy session would silently change the meaning of every
/// existing caller. Auto therefore preserves today's FIFO semantics exactly, and steering is opt-in.
/// </para>
/// <para>
/// <see cref="Steer"/> and <see cref="Interrupt"/> are <em>requests</em>, not guarantees: when no
/// turn is running there is nothing to steer, so the seam falls back to queueing rather than
/// injecting into an idle agent's pending queue (which would never drain — the dead-letter failure
/// the SignalR hub already guards against).
/// </para>
/// </remarks>
public enum InboundDeliveryMode
{
    /// <summary>
    /// Let the gateway apply the documented default. Today that default is
    /// <see cref="Queue"/> for every session, busy or idle — see the type remarks for why
    /// auto-steering is deliberately NOT the default.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Always append to the per-isolation-unit FIFO queue and take a turn when the queue drains,
    /// even if a turn is currently running. Never injects into an in-flight turn.
    /// </summary>
    Queue = 1,

    /// <summary>
    /// Inject into the running turn when one is active, so the agent sees the message at its next
    /// steering drain point. Falls back to <see cref="Queue"/> when no turn is running.
    /// </summary>
    Steer = 2,

    /// <summary>
    /// Abort the running turn, discard stale steering messages, and redirect the agent with this
    /// message. Falls back to <see cref="Queue"/> when no turn is running.
    /// </summary>
    Interrupt = 3
}

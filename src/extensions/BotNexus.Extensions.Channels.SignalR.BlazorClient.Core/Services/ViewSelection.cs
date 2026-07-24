namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Names the origin of a request to change the portal's active view. The source is the
/// authority the store uses to decide whether a selection is allowed to promote a read-only
/// sub-agent session to the active view: only <see cref="SubAgentView"/> may do so, so a
/// concurrent inbound event (SubAgentSpawned, streaming) can never hijack the active view
/// onto a sub-agent session (#2243). Making the intent explicit at every call site replaces
/// the old implicit "allow flag" toggle around the mutable setter.
/// </summary>
public enum SelectionSource
{
    /// <summary>The user clicked an agent in the sidebar / picker. A normal user-driven switch.</summary>
    UserClick,

    /// <summary>Applied from the current route (deep link / navigation), not a direct click.</summary>
    RouteNavigation,

    /// <summary>Initial portal bootstrap picking the first agent after load.</summary>
    Bootstrap,

    /// <summary>
    /// The explicit "view sub-agent" interaction. This is the ONLY source permitted to switch the
    /// active view onto a read-only sub-agent session; every other source is rejected by the store
    /// when the target is read-only.
    /// </summary>
    SubAgentView
}

/// <summary>
/// Immutable description of the portal's active view: which agent (and, when known, which
/// conversation) is selected, together with the <see cref="SelectionSource"/> that requested it.
/// This is the single value the store mutates through <c>SelectView</c>; the public
/// <c>ActiveAgentId</c> / <c>ActiveConversationId</c> members are read-only projections of it,
/// so there is exactly one writer for the active view (#2246).
/// </summary>
/// <param name="AgentId">The selected agent ID, or empty when nothing is selected.</param>
/// <param name="ConversationId">The selected conversation ID, or empty when unspecified.</param>
/// <param name="Source">Which interaction requested this selection.</param>
public sealed record ViewSelection(string AgentId, string ConversationId, SelectionSource Source)
{
    /// <summary>The empty selection — no active agent. Used before the first selection and after the
    /// active agent is removed, at which point the UI re-resolves a selection on the next render.</summary>
    public static readonly ViewSelection None = new(string.Empty, string.Empty, SelectionSource.Bootstrap);
}

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// The single named predicate that answers "is this conversation currently DISPLAYED" (#3212,
/// step 5 of #3061).
///
/// <para>
/// Before this seam existed, every inbound-event visibility decision compared the event's
/// conversation id against the ambient per-agent <c>AgentState.ActiveConversationId</c>. That value
/// is a <em>last-selected</em> marker maintained independently on every agent, so it answered a
/// different question from the one the caller was actually asking: not "is the user looking at this
/// pane" but "was this the last conversation this agent happened to have selected". Nine agents
/// therefore had nine simultaneously-"active" conversations while the browser rendered exactly one.
/// </para>
///
/// <para>
/// This predicate is derived from the route instead. It is implemented by the routing/state layer
/// (<see cref="ClientStateStore"/>) over the single <see cref="ViewSelection"/> that
/// <c>SelectView</c> writes — the same value the route application path
/// (<c>Home.razor.ApplyRouteSelectionAsync</c>, <see cref="SelectionSource.RouteNavigation"/>)
/// establishes. Exactly one (agent, conversation) pair is displayed at a time, which is what the
/// rendered UI actually shows.
/// </para>
///
/// <para>
/// It is injected into <c>GatewayEventHandler</c> so that handler has no ambient visibility source
/// of its own. This is deliberately NOT behaviour-preserving: see the PR body for #3212 and the
/// unread/badge tests it pins.
/// </para>
/// </summary>
public interface IDisplayedConversation
{
    /// <summary>
    /// True when <paramref name="conversationId"/> is the conversation currently displayed for
    /// <paramref name="agentId"/> — that is, when the current route-derived selection names both.
    /// A null/blank conversation id is never displayed: an event that cannot name its conversation
    /// must not be treated as visible (#3065 guarantees conversation-scoped inbound events carry one).
    /// </summary>
    bool IsConversationDisplayed(string? agentId, string? conversationId);

    /// <summary>
    /// True when <paramref name="agentId"/> is the agent whose pane is currently displayed. Used by
    /// the agent-level unread badge, which counts activity on agents the user is not looking at.
    /// </summary>
    bool IsAgentDisplayed(string? agentId);

    /// <summary>
    /// The displayed conversation id for <paramref name="agentId"/>, or <see langword="null"/> when
    /// that agent is not the displayed one. This is the route-derived replacement for reads of
    /// <c>AgentState.ActiveConversationId</c> on recovery paths (session reset, reconnect) that need
    /// to know which pane the user is actually staring at, and it is intentionally null for every
    /// agent except the displayed one.
    /// </summary>
    string? DisplayedConversationIdFor(string? agentId);
}

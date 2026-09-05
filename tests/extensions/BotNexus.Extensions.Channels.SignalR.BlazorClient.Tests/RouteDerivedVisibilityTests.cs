using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #3212 (step 5 of #3061): pins the route-derived visibility predicate that replaced
/// <c>GatewayEventHandler</c>'s ambient <c>AgentState.ActiveConversationId</c> checks.
///
/// <para>
/// This step is explicitly NOT behaviour-preserving, so the new behaviour is asserted here rather
/// than inferred. Two changes are pinned:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Unread/badge is now a visibility question.</b> Previously a conversation counted as "read"
/// whenever it matched its OWN agent's last-selected marker — so an event on agent B's
/// last-selected conversation incremented nothing even though the user was looking at agent A.
/// Now only the single route-displayed (agent, conversation) pair suppresses unread; every other
/// conversation, including a non-displayed conversation of the DISPLAYED agent and any
/// conversation of another agent, accrues unread.
/// </description></item>
/// <item><description>
/// <b>The attribution fallback is deleted.</b> An event that names no conversation and whose
/// session is unregistered is DROPPED, not attributed to an arbitrary conversation.
/// </description></item>
/// </list>
/// </summary>
public sealed class RouteDerivedVisibilityTests
{
    private readonly ClientStateStore _store = new();
    private readonly GatewayEventHandler _handler;

    public RouteDerivedVisibilityTests()
    {
        _handler = new GatewayEventHandler(
            _store, new GatewayHubConnection(), NullLogger<GatewayEventHandler>.Instance, _store);

        // Two agents. agent-1 has the displayed conversation (conv-shown) plus a second,
        // non-displayed one (conv-hidden). agent-2 is an entirely different agent.
        _store.UpsertAgent(new AgentState { AgentId = "agent-1", DisplayName = "One", IsConnected = true });
        _store.UpsertAgent(new AgentState { AgentId = "agent-2", DisplayName = "Two", IsConnected = true });

        var a1 = _store.GetAgent("agent-1")!;
        a1.Conversations["conv-shown"] = new ConversationState { ConversationId = "conv-shown", Title = "Shown", ActiveSessionId = "sess-shown" };
        a1.Conversations["conv-hidden"] = new ConversationState { ConversationId = "conv-hidden", Title = "Hidden", ActiveSessionId = "sess-hidden" };

        var a2 = _store.GetAgent("agent-2")!;
        a2.Conversations["conv-other"] = new ConversationState { ConversationId = "conv-other", Title = "Other", ActiveSessionId = "sess-other" };

        _store.RegisterSession("agent-1", "sess-shown");
        _store.RegisterSession("agent-1", "sess-hidden");
        _store.RegisterSession("agent-2", "sess-other");

        // THE route. Exactly one (agent, conversation) pair is displayed.
        _store.SelectView("agent-1", "conv-shown", SelectionSource.RouteNavigation);

        // #3061's premise: every agent independently carries a last-selected marker, and BOTH of
        // these were previously treated as "active". Setting them here is what makes the three
        // assertions below meaningful rather than vacuous -- under the old ambient logic
        // conv-hidden and conv-other would BOTH have been treated as read.
        a1.ActiveConversationId = "conv-hidden";
        a2.ActiveConversationId = "conv-other";
    }

    private static AgentStreamEvent Reply(string sessionId, string conversationId) => new()
    {
        SessionId = sessionId,
        ConversationId = conversationId
    };

    // ── AC4 case 1: the DISPLAYED conversation ───────────────────────────────

    [Fact]
    public void MessageEnd_on_the_displayed_conversation_accrues_no_unread()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-shown"];
        conv.StreamState.Buffer = "visible reply";

        _handler.HandleMessageEnd(Reply("sess-shown", "conv-shown"));

        Assert.Equal(0, conv.UnreadCount);
        Assert.Equal(0, agent.UnreadCount);
    }

    // ── AC4 case 2: a NON-DISPLAYED conversation of the DISPLAYED agent ──────

    [Fact]
    public void MessageEnd_on_a_non_displayed_conversation_of_the_displayed_agent_accrues_conversation_unread_only()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-hidden"];
        conv.StreamState.Buffer = "background reply";

        _handler.HandleMessageEnd(Reply("sess-hidden", "conv-hidden"));

        // #3212 behaviour change: conv-hidden IS this agent's ActiveConversationId, so the old
        // ambient check scored it read and incremented nothing. It is not on screen, so it now
        // accrues unread.
        Assert.Equal(1, conv.UnreadCount);

        // The agent badge stays clear: agent-1 IS the displayed agent, so its pane is on screen.
        Assert.Equal(0, agent.UnreadCount);
    }

    // ── AC4 case 3: a conversation of ANOTHER agent ──────────────────────────

    [Fact]
    public void MessageEnd_on_another_agents_conversation_accrues_both_conversation_and_agent_unread()
    {
        var other = _store.GetAgent("agent-2")!;
        var conv = other.Conversations["conv-other"];
        conv.StreamState.Buffer = "other agent reply";

        _handler.HandleMessageEnd(Reply("sess-other", "conv-other"));

        // conv-other is agent-2's own ActiveConversationId, so the old ambient check scored it
        // read. Under the route-derived predicate it is not displayed, so it accrues unread.
        Assert.Equal(1, conv.UnreadCount);
        Assert.Equal(1, other.UnreadCount);
    }

    [Fact]
    public void ToolStart_and_ToolEnd_follow_the_same_route_derived_unread_rule()
    {
        var agent = _store.GetAgent("agent-1")!;
        var shown = agent.Conversations["conv-shown"];
        var hidden = agent.Conversations["conv-hidden"];

        _handler.HandleToolStart(new AgentStreamEvent { SessionId = "sess-shown", ConversationId = "conv-shown", ToolCallId = "t1", ToolName = "read" });
        _handler.HandleToolStart(new AgentStreamEvent { SessionId = "sess-hidden", ConversationId = "conv-hidden", ToolCallId = "t2", ToolName = "read" });

        Assert.Equal(0, shown.UnreadCount);
        Assert.Equal(1, hidden.UnreadCount);

        // ToolEnd on a tool-call id the conversation does not know takes the append-new-message
        // fallback, which carries the same unread decision.
        _handler.HandleToolEnd(new AgentStreamEvent { SessionId = "sess-shown", ConversationId = "conv-shown", ToolCallId = "unknown", ToolName = "read" });
        _handler.HandleToolEnd(new AgentStreamEvent { SessionId = "sess-hidden", ConversationId = "conv-hidden", ToolCallId = "unknown", ToolName = "read" });

        Assert.Equal(0, shown.UnreadCount);
        Assert.Equal(2, hidden.UnreadCount);
    }

    // ── AC2: the attribution fallback is DELETED, not rerouted ───────────────

    [Fact]
    public void MessageEnd_without_a_conversation_id_is_dropped_not_attributed_to_an_arbitrary_conversation()
    {
        // #3065 (closed) guarantees conversation-scoped inbound events carry a conversation id.
        // An event that still names none, on a session bound to no conversation, previously fell
        // back to agent.ActiveConversationId -- appending another conversation's reply into
        // whichever pane the agent had last selected. That fallback is deleted: the event is
        // DROPPED. Registering an unbound session is what removes the session->conversation
        // route, leaving the deleted fallback as the ONLY thing that could have attributed it.
        RegisterUnboundSession("sess-unbound");
        var agent = _store.GetAgent("agent-1")!;
        var shown = agent.Conversations["conv-shown"];
        var hidden = agent.Conversations["conv-hidden"];
        var shownBefore = shown.Messages.Count;
        var hiddenBefore = hidden.Messages.Count;

        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-unbound", ConversationId = null });

        Assert.Equal(shownBefore, shown.Messages.Count);
        Assert.Equal(hiddenBefore, hidden.Messages.Count);
        Assert.Equal(0, shown.UnreadCount);
        Assert.Equal(0, hidden.UnreadCount);
    }

    [Fact]
    public void Error_without_a_conversation_id_is_dropped_not_painted_into_an_arbitrary_conversation()
    {
        RegisterUnboundSession("sess-unbound");
        var agent = _store.GetAgent("agent-1")!;

        _handler.HandleError(new AgentStreamEvent { SessionId = "sess-unbound", ConversationId = null, ErrorMessage = "boom" });

        Assert.DoesNotContain(agent.Conversations["conv-shown"].Messages, m => m.Role == "Error");
        Assert.DoesNotContain(agent.Conversations["conv-hidden"].Messages, m => m.Role == "Error");

        // The agent-level streaming flags are still cleared -- dropping the ATTRIBUTION must not
        // strand the portal on a perpetual streaming indicator.
        Assert.False(agent.IsStreaming);
        Assert.Null(agent.ProcessingStage);
    }

    [Fact]
    public void SteeringFeedback_without_a_conversation_id_is_dropped_not_attributed()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.SessionId = "sess-unbound";
        var shownBefore = agent.Conversations["conv-shown"].Messages.Count;
        var hiddenBefore = agent.Conversations["conv-hidden"].Messages.Count;

        _handler.HandleSteeringFeedback(new SteeringFeedbackPayload("agent-1", "sess-unbound", SteeringFeedbackKind.Injected));

        Assert.Equal(shownBefore, agent.Conversations["conv-shown"].Messages.Count);
        Assert.Equal(hiddenBefore, agent.Conversations["conv-hidden"].Messages.Count);
    }

    // ── AC1/AC3: the predicate is the single visibility source ───────────────

    [Fact]
    public void GatewayEventHandler_source_contains_no_reference_to_ActiveConversationId()
    {
        // AC1 is a source-level guarantee: the handler must not reacquire an ambient visibility
        // source. Comments naming the property (explaining WHY it is not read) are permitted;
        // an actual member access is not.
        var source = ReadHandlerSource();

        var offending = source
            .Split('\n')
            .Select((line, index) => (Line: line.Trim(), Number: index + 1))
            .Where(x => x.Line.Contains("ActiveConversationId", StringComparison.Ordinal))
            .Where(x => !x.Line.StartsWith("//", StringComparison.Ordinal)
                        && !x.Line.StartsWith("///", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            offending.Count == 0,
            "GatewayEventHandler must derive visibility from IDisplayedConversation only (#3212 AC1). " +
            "Offending lines: " + string.Join(" | ", offending.Select(x => $"{x.Number}: {x.Line}")));
    }

    [Fact]
    public void ResolveConversationId_has_no_ambient_attribution_fallback()
    {
        // AC2 as a source guarantee: the deleted fallback must not reappear in any spelling.
        // Comment lines are excluded -- the remarks on ResolveConversationId quote the deleted
        // expression in order to explain why it must not return, and that documentation is the
        // point, not a violation.
        var code = string.Join(
            "\n",
            ReadHandlerSource()
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => !l.StartsWith("//", StringComparison.Ordinal)));

        Assert.DoesNotContain("?? agent!.ActiveConversationId", code, StringComparison.Ordinal);
        Assert.DoesNotContain("?? agent.ActiveConversationId", code, StringComparison.Ordinal);
        Assert.DoesNotContain("??= agent.ActiveConversationId", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Registers a session bound to NO conversation, which is what leaves the deleted attribution
    /// fallback as the only thing that could ever have attributed an event carrying no conversation id.
    /// </summary>
    /// <remarks>
    /// <c>RegisterSession</c>'s legacy single-establish path (#314) stamps the new session onto the
    /// agent's <c>ActiveConversationId</c> conversation. That binding would let
    /// <c>TryResolveConversationBySession</c> answer, so the event would route legitimately and the
    /// test would prove nothing about the fallback. Restoring the real session bindings afterwards
    /// removes that route and makes the assertion non-vacuous.
    /// </remarks>
    private void RegisterUnboundSession(string sessionId)
    {
        _store.RegisterSession("agent-1", sessionId);
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-shown"].ActiveSessionId = "sess-shown";
        agent.Conversations["conv-hidden"].ActiveSessionId = "sess-hidden";

        Assert.False(
            _store.TryResolveConversationBySession("agent-1", sessionId, out _),
            "the session must resolve to NO conversation, or the fallback is not the path under test");
    }

    private static string ReadHandlerSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        Assert.NotNull(dir);

        var path = Path.Combine(
            dir!.FullName,
            "src", "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient.Core",
            "Services", "GatewayEventHandler.cs");

        Assert.True(File.Exists(path), $"Could not locate GatewayEventHandler.cs (looked at {path}).");
        return File.ReadAllText(path);
    }
}

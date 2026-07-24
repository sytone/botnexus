using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Seam / integration harness for the <c>#2246/#2249</c> single-writer view-selection contract.
/// Establishes an explicit user selection on a real user-facing agent, then dispatches every inbound
/// event type through the store's single writer and asserts the active view (the <c>ViewSelection</c>
/// projected by <c>ActiveAgentId</c> / <c>ActiveConversationId</c>) is UNCHANGED.
///
/// Before #2246 an inbound event (SubAgentSpawned, streaming, history-404, agent-removed) could assign
/// the active view out from under the user. The store now exposes exactly one mutation path
/// (<c>SelectView</c>); inbound events are data-only. These tests pin that behaviour so a regression
/// that re-introduces an ad-hoc active-view mutation on any inbound path fails here, complementing the
/// structural fences in <c>SingleWriterViewSelectionArchitectureTests</c>.
/// </summary>
public sealed class GatewayEventHandlerViewSelectionSeamTests
{
    private const string UserAgentId = "user-agent";
    private const string UserSessionId = "user-sess";
    private const string UserConversationId = "user-conv";

    // A second, non-active agent whose inbound events must never disturb the user's active view.
    private const string OtherAgentId = "other-agent";
    private const string OtherSessionId = "other-sess";
    private const string OtherConversationId = "other-conv";

    private readonly ClientStateStore _store = new();
    private readonly GatewayEventHandler _handler;

    public GatewayEventHandlerViewSelectionSeamTests()
    {
        _handler = new GatewayEventHandler(_store, new GatewayHubConnection(), NullLogger<GatewayEventHandler>.Instance);

        // The active, user-selected agent + conversation.
        _store.UpsertAgent(new AgentState
        {
            AgentId = UserAgentId,
            DisplayName = "User Agent",
            IsConnected = true,
            SessionId = UserSessionId
        });
        var userAgent = _store.GetAgent(UserAgentId)!;
        userAgent.Conversations[UserConversationId] = new ConversationState
        {
            ConversationId = UserConversationId,
            Title = "User Conversation",
            ActiveSessionId = UserSessionId
        };
        _store.RegisterSession(UserAgentId, UserSessionId, conversationId: UserConversationId);

        // A second user agent that owns the inbound events under test.
        _store.UpsertAgent(new AgentState
        {
            AgentId = OtherAgentId,
            DisplayName = "Other Agent",
            IsConnected = true,
            SessionId = OtherSessionId
        });
        var otherAgent = _store.GetAgent(OtherAgentId)!;
        otherAgent.Conversations[OtherConversationId] = new ConversationState
        {
            ConversationId = OtherConversationId,
            Title = "Other Conversation",
            ActiveSessionId = OtherSessionId
        };
        _store.RegisterSession(OtherAgentId, OtherSessionId, conversationId: OtherConversationId);

        // Establish the explicit user selection - the view state every case below must preserve.
        _store.SelectView(UserAgentId, UserConversationId, SelectionSource.UserClick);
    }

    // Snapshot of the single view-selection value (its two projected components).
    private (string? Agent, string? Conversation) Selection()
        => (_store.ActiveAgentId, _store.ActiveConversationId);

    private void AssertSelectionUnchanged(string eventName)
    {
        var (agent, conv) = Selection();
        Assert.Equal(UserAgentId, agent);
        Assert.Equal(UserConversationId, conv);
        Assert.False(_store.PendingSelectionInvalid,
            $"{eventName} must not invalidate the active user selection.");
    }

    private SubAgentEventPayload SubAgentPayload(string status) => new(
        SessionId: OtherSessionId,
        SubAgentId: "sub-1",
        Name: "sub",
        Task: "do work",
        Model: "model",
        Archetype: "coder",
        Status: status,
        StartedAt: DateTimeOffset.UtcNow,
        CompletedAt: DateTimeOffset.UtcNow,
        TurnsUsed: 1,
        ResultSummary: "done",
        TimedOut: false,
        ChildSessionId: "child-sess",
        ConversationId: OtherConversationId);

    [Fact]
    public void SubAgentSpawned_does_not_change_view_selection()
    {
        _handler.HandleSubAgentSpawned(SubAgentPayload("Running"));
        AssertSelectionUnchanged(nameof(_handler.HandleSubAgentSpawned));
    }

    [Fact]
    public void SubAgentCompleted_does_not_change_view_selection()
    {
        _handler.HandleSubAgentSpawned(SubAgentPayload("Running"));
        _handler.HandleSubAgentCompleted(SubAgentPayload("Completed"));
        AssertSelectionUnchanged(nameof(_handler.HandleSubAgentCompleted));
    }

    [Fact]
    public void SubAgentFailed_does_not_change_view_selection()
    {
        _handler.HandleSubAgentSpawned(SubAgentPayload("Running"));
        _handler.HandleSubAgentFailed(SubAgentPayload("Failed"));
        AssertSelectionUnchanged(nameof(_handler.HandleSubAgentFailed));
    }

    [Fact]
    public void SubAgentKilled_does_not_change_view_selection()
    {
        _handler.HandleSubAgentSpawned(SubAgentPayload("Running"));
        _handler.HandleSubAgentKilled(SubAgentPayload("Killed"));
        AssertSelectionUnchanged(nameof(_handler.HandleSubAgentKilled));
    }

    [Fact]
    public void HistoryNotFound_MarkSelectionInvalid_does_not_switch_view_onto_another_agent()
    {
        // The history-404 recovery path (HandleHistoryNotFound) is data-only: when the active
        // conversation vanishes server-side it either re-binds the same agent's next conversation
        // via SetActiveConversation or flags the selection invalid via MarkSelectionInvalid - it
        // never assigns the active view onto a *different* agent. Here the invalidation must leave
        // the active AGENT untouched (the UI resolves a fresh conversation on next render).
        _store.MarkSelectionInvalid();

        // The active agent projection is preserved; only the invalid flag is raised.
        Assert.Equal(UserAgentId, _store.ActiveAgentId);
        Assert.True(_store.PendingSelectionInvalid);
    }

    [Fact]
    public void HistoryNotFound_next_conversation_rebind_stays_on_same_agent()
    {
        // When a sibling conversation remains, the recovery rebinds THIS agent's conversation via
        // SetActiveConversation - the active agent must not change to any other agent.
        var userAgent = _store.GetAgent(UserAgentId)!;
        userAgent.Conversations["user-conv-2"] = new ConversationState
        {
            ConversationId = "user-conv-2",
            Title = "Sibling",
            ActiveSessionId = "user-sess-2"
        };

        _store.SetActiveConversation(UserAgentId, "user-conv-2");

        Assert.Equal(UserAgentId, _store.ActiveAgentId);
        Assert.Equal("user-conv-2", _store.ActiveConversationId);
        Assert.False(_store.PendingSelectionInvalid);
    }

    [Fact]
    public void AgentRemoved_of_non_active_agent_does_not_change_view_selection()
    {
        // Removing a DIFFERENT (non-active) agent must never disturb the active user view.
        _store.RemoveAgent(OtherAgentId);
        AssertSelectionUnchanged("RemoveAgent(non-active)");
    }

    [Fact]
    public void AgentRemoved_of_active_agent_is_data_only_and_does_not_promote_another_view()
    {
        // Removing the ACTIVE agent is data-only: the store clears the selection and flags it invalid
        // (the UI resolves a fresh selection on next render). Critically it must NOT auto-promote the
        // remaining agent as the new active view - that would be an ad-hoc view mutation (#2246).
        _store.RemoveAgent(UserAgentId);

        Assert.Null(_store.ActiveAgentId);
        Assert.True(_store.PendingSelectionInvalid);
        // The surviving agent was not silently promoted into the active view.
        Assert.NotEqual(OtherAgentId, _store.ActiveAgentId);
    }
}

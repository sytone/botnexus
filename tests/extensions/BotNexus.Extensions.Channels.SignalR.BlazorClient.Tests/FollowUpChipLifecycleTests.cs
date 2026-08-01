using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #2439: the pending FOLLOW-UP chip must have a real lifecycle. Before this suite the ONLY
/// clear signal was a <c>SteeringFeedback(Injected)</c> event, and the gateway never emits a
/// steering-feedback event for a queued follow-up — so a follow-up chip was set and nothing
/// could ever clear it.
///
/// The clear state is DERIVED from signals the gateway already emits (RunEnded, SessionReset,
/// reconnect) plus local user intent (abort, conversation switch). No new activity type, no new
/// stored flag that some path must remember to write.
/// </summary>
public sealed class FollowUpChipLifecycleTests
{
    private readonly ClientStateStore _store = new();
    private readonly GatewayEventHandler _handler;

    public FollowUpChipLifecycleTests()
    {
        _handler = new GatewayEventHandler(_store, new GatewayHubConnection(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GatewayEventHandler>.Instance);

        _store.UpsertAgent(new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Agent 1",
            IsConnected = true,
            SessionId = "sess-1",
            ActiveConversationId = "conv-1"
        });

        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Conversation 1",
            ActiveSessionId = "sess-1"
        };
        agent.Conversations["conv-2"] = new ConversationState
        {
            ConversationId = "conv-2",
            Title = "Conversation 2",
            ActiveSessionId = "sess-2"
        };
        _store.RegisterSession("agent-1", "sess-1");
    }

    private void QueueFollowUp(string conversationId = "conv-1", string id = "f1", string text = "pending follow-up") =>
        _store.AddSteeringEntry(conversationId,
            new SteeringEntry(id, text, SteeringEntryKind.FollowUp, SteeringEntryStatus.Pending));

    // ── AC1 / AC4: the chip clears when the queued follow-up is injected ──────────────────
    //
    // RunEnded is the authoritative "the run loop fully settled" signal, and the agent loop
    // drains the follow-up queue BEFORE the run can end (AgentLoopRunner: a drained follow-up
    // seeds another turn and the loop continues). So at RunEnded nothing can still be pending —
    // whatever was queued was either injected or discarded. That is the cheapest honest
    // injection signal, and it already exists.

    [Fact]
    public void RunEnded_clears_a_pending_followup_chip()
    {
        QueueFollowUp();
        Assert.Single(_store.GetSteeringQueue("conv-1"));

        _handler.HandleRunEnded(new AgentStreamEvent { SessionId = "sess-1", ConversationId = "conv-1" });

        Assert.Empty(_store.GetSteeringQueue("conv-1"));
    }

    [Fact]
    public void RunEnded_with_misrouted_conversation_hint_still_clears_the_active_conversation_chip()
    {
        // Mirrors the #2195 hardening on the run bracket: a stale/misrouted ConversationId must
        // not strand the chip. A pending indicator that cannot be verified must not persist.
        QueueFollowUp();

        _handler.HandleRunEnded(new AgentStreamEvent { SessionId = "sess-1", ConversationId = "conv-does-not-exist" });

        Assert.Empty(_store.GetSteeringQueue("conv-1"));
    }

    [Fact]
    public void RunEnded_also_clears_a_pending_steer_chip()
    {
        _store.AddSteeringEntry("conv-1",
            new SteeringEntry("s1", "steer me", SteeringEntryKind.Steer, SteeringEntryStatus.Pending));

        _handler.HandleRunEnded(new AgentStreamEvent { SessionId = "sess-1", ConversationId = "conv-1" });

        Assert.Empty(_store.GetSteeringQueue("conv-1"));
    }

    [Fact]
    public void RunEnded_does_not_clear_another_conversations_chip()
    {
        QueueFollowUp("conv-2", "f2", "other conversation follow-up");
        QueueFollowUp();

        _handler.HandleRunEnded(new AgentStreamEvent { SessionId = "sess-1", ConversationId = "conv-1" });

        Assert.Empty(_store.GetSteeringQueue("conv-1"));
        Assert.Single(_store.GetSteeringQueue("conv-2"));
    }

    // ── AC5: clear-on-injection via the existing steering-feedback path still works ────────

    [Fact]
    public void SteeringFeedback_Injected_clears_a_pending_followup_chip()
    {
        QueueFollowUp();

        _handler.HandleSteeringFeedback(new SteeringFeedbackPayload(
            AgentId: "agent-1", SessionId: "sess-1",
            Kind: SteeringFeedbackKind.Injected, ConversationId: "conv-1"));

        Assert.Empty(_store.GetSteeringQueue("conv-1"));
    }

    // ── AC2: session reset ────────────────────────────────────────────────────────────────

    [Fact]
    public void SessionReset_clears_the_pending_chip()
    {
        QueueFollowUp();

        _handler.HandleSessionReset(new SessionResetPayload("agent-1", "sess-1", "conv-1"));

        Assert.Empty(_store.GetSteeringQueue("conv-1"));
    }

    // ── AC2: turn interrupted (gateway restart kills the run; no RunEnded will arrive) ────

    [Fact]
    public void TurnInterrupted_clears_the_pending_chip()
    {
        QueueFollowUp();

        _handler.HandleTurnInterrupted(new AgentStreamEvent
        {
            SessionId = "sess-1",
            ConversationId = "conv-1",
            ErrorMessage = "gateway restarted"
        });

        Assert.Empty(_store.GetSteeringQueue("conv-1"));
    }

    // ── AC2: conversation switch ──────────────────────────────────────────────────────────

    [Fact]
    public void Switching_conversation_clears_the_chip_on_the_conversation_being_left()
    {
        QueueFollowUp();

        _store.SetActiveConversation("agent-1", "conv-2");

        Assert.Empty(_store.GetSteeringQueue("conv-1"));
    }

    [Fact]
    public void Switching_conversation_does_not_clear_the_chip_on_the_conversation_being_entered()
    {
        QueueFollowUp("conv-2", "f2", "target conversation follow-up");

        _store.SetActiveConversation("agent-1", "conv-2");

        Assert.Single(_store.GetSteeringQueue("conv-2"));
    }

    // ── AC3: reconnect where pending state cannot be confirmed ───────────────────────────

    [Fact]
    public async Task Reconnect_clears_pending_chips_that_cannot_be_confirmed()
    {
        QueueFollowUp();
        QueueFollowUp("conv-2", "f2", "other pending");

        // SubscribeAllAsync throws (no live hub); the handler must still clear unverifiable state.
        await _handler.HandleReconnectedAsync();

        Assert.Empty(_store.GetSteeringQueue("conv-1"));
        Assert.Empty(_store.GetSteeringQueue("conv-2"));
    }

    // ── Store primitive ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ClearSteeringQueue_is_idempotent_and_safe_for_unknown_conversations()
    {
        QueueFollowUp();

        _store.ClearSteeringQueue("conv-1");
        _store.ClearSteeringQueue("conv-1");
        _store.ClearSteeringQueue("no-such-conversation");

        Assert.Empty(_store.GetSteeringQueue("conv-1"));
    }
}

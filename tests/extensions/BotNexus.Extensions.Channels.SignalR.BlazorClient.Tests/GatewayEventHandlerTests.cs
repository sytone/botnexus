using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class GatewayEventHandlerTests
{
    private readonly ClientStateStore _store = new();
    private readonly GatewayEventHandler _handler;

    public GatewayEventHandlerTests()
    {
        _handler = new GatewayEventHandler(_store, new GatewayHubConnection(), Microsoft.Extensions.Logging.Abstractions.NullLogger<GatewayEventHandler>.Instance);

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
        _store.RegisterSession("agent-1", "sess-1");
    }

    [Fact]
    public void HandleMessageStart_sets_streaming_state_and_clears_buffers()
    {
        var conv = _store.GetAgent("agent-1")!.Conversations["conv-1"];
        conv.StreamState.Buffer = "old";
        conv.StreamState.ThinkingBuffer = "old-think";

        _handler.HandleMessageStart(new AgentStreamEvent { SessionId = "sess-1" });

        Assert.True(_store.GetAgent("agent-1")!.IsStreaming);
        Assert.True(conv.StreamState.IsStreaming);
        Assert.Equal(string.Empty, conv.StreamState.Buffer);
        Assert.Equal(string.Empty, conv.StreamState.ThinkingBuffer);
    }

    [Fact]
    public void HandleContentDelta_appends_content()
    {
        var conv = _store.GetAgent("agent-1")!.Conversations["conv-1"];

        _handler.HandleContentDelta(new AgentStreamEvent { SessionId = "sess-1", ContentDelta = "hello" });
        _handler.HandleContentDelta(new AgentStreamEvent { SessionId = "sess-1", ContentDelta = " world" });

        Assert.Equal("hello world", conv.StreamState.Buffer);
    }

    [Fact]
    public void HandleThinkingDelta_appends_thinking()
    {
        var conv = _store.GetAgent("agent-1")!.Conversations["conv-1"];

        _handler.HandleThinkingDelta(new AgentStreamEvent { SessionId = "sess-1", ThinkingContent = "plan" });

        Assert.Equal("plan", conv.StreamState.ThinkingBuffer);
        Assert.Equal("💭 Thinking…", _store.GetAgent("agent-1")!.ProcessingStage);
    }

    [Fact]
    public void HandleToolStart_adds_tool_message_and_tracks_active_call()
    {
        var conv = _store.GetAgent("agent-1")!.Conversations["conv-1"];

        _handler.HandleToolStart(new AgentStreamEvent
        {
            SessionId = "sess-1",
            ToolCallId = "tool-1",
            ToolName = "search",
            ToolArgs = new Dictionary<string, object?> { ["q"] = "wave3" }
        });

        Assert.Single(conv.Messages);
        Assert.True(conv.Messages[0].IsToolCall);
        Assert.True(conv.StreamState.ActiveToolCalls.ContainsKey("tool-1"));
    }

    [Fact]
    public void HandleMessageEnd_commits_assistant_message_and_clears_streaming()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        agent.IsStreaming = true;
        conv.StreamState.IsStreaming = true;
        conv.StreamState.Buffer = "done";
        conv.StreamState.ThinkingBuffer = "thinking";

        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });

        Assert.False(agent.IsStreaming);
        Assert.False(conv.StreamState.IsStreaming);
        Assert.Equal(string.Empty, conv.StreamState.Buffer);
        Assert.Equal(string.Empty, conv.StreamState.ThinkingBuffer);
        Assert.Single(conv.Messages);
        Assert.Equal("Assistant", conv.Messages[0].Role);
        Assert.Equal("done", conv.Messages[0].Content);
        Assert.Equal("thinking", conv.Messages[0].ThinkingContent);
    }

    // #1651 (post-as-assistant Step 3/3): the live SignalR fan-out now carries the stamped
    // MessageRole on the ContentDelta event (previously the DTO dropped it and every buffered
    // post flushed as "Assistant"). A ContentDelta that carries an explicit role must record
    // it on the stream state so the terminal flush honours it -- this is the seam that lets a
    // user-stamped agent-post render as a user bubble on the live path.
    [Fact]
    public void HandleContentDelta_records_delivered_role_on_stream_state()
    {
        var conv = _store.GetAgent("agent-1")!.Conversations["conv-1"];

        _handler.HandleContentDelta(new AgentStreamEvent { SessionId = "sess-1", ContentDelta = "on behalf", Role = "user" });

        Assert.Equal("user", conv.StreamState.PendingRole);
    }

    // #1651: when the buffered content was delivered with an explicit `user` role (the
    // on-behalf-of-user kickoff), the flush must commit a USER bubble, not an assistant one.
    [Fact]
    public void HandleMessageEnd_honours_user_role_carried_on_the_delta()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        agent.IsStreaming = true;

        _handler.HandleContentDelta(new AgentStreamEvent { SessionId = "sess-1", ContentDelta = "kickoff", Role = "user" });
        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });

        Assert.Single(conv.Messages);
        Assert.Equal("User", conv.Messages[0].Role);
        Assert.Equal("kickoff", conv.Messages[0].Content);
        // Pending role must be cleared with the rest of the stream buffers so the next
        // (default) turn is not mis-stamped.
        Assert.Null(conv.StreamState.PendingRole);
    }

    // #1651: no regression -- a delta with no role (the overwhelming common case: the agent's
    // own streamed reply, or any fan-out that does not stamp a role) still flushes as Assistant.
    [Fact]
    public void HandleMessageEnd_defaults_to_assistant_when_no_role_carried()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        agent.IsStreaming = true;

        _handler.HandleContentDelta(new AgentStreamEvent { SessionId = "sess-1", ContentDelta = "hi there" });
        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });

        Assert.Single(conv.Messages);
        Assert.Equal("Assistant", conv.Messages[0].Role);
    }

    // PR1.5 (#682): explicit ConversationId on the event must take precedence over the
    // session→conversation lookup so post-compaction stream events (which carry the
    // original conversation but a new sessionId not yet known to the client) still
    // land in the right ConversationState.
    [Fact]
    public void HandleContentDelta_uses_evt_ConversationId_over_session_lookup_after_compaction()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];

        // The server has compacted into a new session ("sess-2") that the client has
        // not yet registered. The event carries the original ConversationId.
        Assert.False(_store.TryResolveConversationBySession("agent-1", "sess-2", out _));

        _handler.HandleContentDelta(new AgentStreamEvent
        {
            SessionId = "sess-2",
            ConversationId = "conv-1",
            ContentDelta = "post-compaction-chunk"
        });

        Assert.Equal("post-compaction-chunk", conv.StreamState.Buffer);
    }

    // ---- IsTurnActive tests (issue #253 steering flicker fix) ----

    [Fact]
    public void IsTurnActive_is_true_when_streaming()
    {
        var conv = _store.GetAgent("agent-1")!.Conversations["conv-1"];
        conv.StreamState.IsStreaming = true;

        Assert.True(conv.StreamState.IsTurnActive);
    }

    [Fact]
    public void IsTurnActive_is_false_when_not_streaming_and_no_tool_calls()
    {
        var conv = _store.GetAgent("agent-1")!.Conversations["conv-1"];
        conv.StreamState.IsStreaming = false;
        conv.StreamState.ActiveToolCalls.Clear();

        Assert.False(conv.StreamState.IsTurnActive);
    }

    [Fact]
    public void IsTurnActive_is_true_after_message_end_when_tool_calls_still_pending()
    {
        // Arrange: simulate the flicker window -- MessageEnd fired but ToolEnd has not yet
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        agent.IsStreaming = true;
        conv.StreamState.IsStreaming = true;
        conv.StreamState.Buffer = "I will search for that.";

        // ToolStart fired during stream -- tool is tracked
        _handler.HandleToolStart(new AgentStreamEvent
        {
            SessionId = "sess-1",
            ToolCallId = "tool-abc",
            ToolName = "web_search"
        });

        // MessageEnd fires -- LLM generation done, but tool is still in flight
        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });

        // IsStreaming is now false on the stream state
        Assert.False(conv.StreamState.IsStreaming);
        // But the tool call is still registered
        Assert.True(conv.StreamState.ActiveToolCalls.ContainsKey("tool-abc"));
        // IsTurnActive must be true so portal keeps Steer/Abort visible
        Assert.True(conv.StreamState.IsTurnActive);
    }

    [Fact]
    public void IsTurnActive_is_false_after_tool_end_and_message_end_both_complete()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        agent.IsStreaming = true;
        conv.StreamState.IsStreaming = true;
        conv.StreamState.Buffer = "done";

        _handler.HandleToolStart(new AgentStreamEvent
        {
            SessionId = "sess-1",
            ToolCallId = "tool-xyz",
            ToolName = "read"
        });
        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });
        _handler.HandleToolEnd(new AgentStreamEvent
        {
            SessionId = "sess-1",
            ToolCallId = "tool-xyz",
            ToolName = "read",
            ToolResult = "file contents"
        });

        // After both MessageEnd and ToolEnd: turn is fully complete
        Assert.False(conv.StreamState.IsStreaming);
        Assert.False(conv.StreamState.ActiveToolCalls.ContainsKey("tool-xyz"));
        Assert.False(conv.StreamState.IsTurnActive);
    }

    // ---- RunStarted/RunEnded authoritative bracket tests (steering flicker, full fix) ----

    [Fact]
    public void HandleRunStarted_marks_run_active_and_streaming()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];

        _handler.HandleRunStarted(new AgentStreamEvent { SessionId = "sess-1" });

        Assert.True(conv.StreamState.IsRunActive);
        Assert.True(conv.StreamState.IsTurnActive);
        Assert.True(agent.IsStreaming);
    }

    [Fact]
    public void HandleRunStarted_clears_residual_buffers_from_previous_turn()
    {
        // Streaming-flash regression: RunStarted fires before the first MessageStart. If a prior
        // turn left content in the buffers, ChatPanel's live streaming bubble would paint it as
        // RAW text (no Markdown pipeline) in the pre-token window -- the reported "previous reply
        // flashes as raw markdown right after I send". RunStarted must clear the buffers so a new
        // run never inherits stale content.
        var conv = _store.GetAgent("agent-1")!.Conversations["conv-1"];
        conv.StreamState.Buffer = "# leftover **markdown** from the last turn";
        conv.StreamState.ThinkingBuffer = "stale thinking";
        conv.StreamState.PendingRole = "assistant";

        _handler.HandleRunStarted(new AgentStreamEvent { SessionId = "sess-1" });

        Assert.Equal(string.Empty, conv.StreamState.Buffer);
        Assert.Equal(string.Empty, conv.StreamState.ThinkingBuffer);
        Assert.Null(conv.StreamState.PendingRole);
    }

    [Fact]
    public void IsTurnActive_stays_true_across_message_end_to_tool_start_gap_while_run_active()
    {
        // The gap the inference signal could not bridge: MessageEnd fires (IsStreaming=false) but
        // ToolStart has not arrived yet (ActiveToolCalls empty). With RunStarted asserted, the
        // controls must stay visible.
        var conv = _store.GetAgent("agent-1")!.Conversations["conv-1"];

        _handler.HandleRunStarted(new AgentStreamEvent { SessionId = "sess-1" });
        _handler.HandleMessageStart(new AgentStreamEvent { SessionId = "sess-1" });
        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });

        // No tool tracked yet, not streaming -- the old inference would say idle here.
        Assert.False(conv.StreamState.IsStreaming);
        Assert.Empty(conv.StreamState.ActiveToolCalls);
        // But the run bracket keeps it active.
        Assert.True(conv.StreamState.IsTurnActive);
    }

    [Fact]
    public void IsTurnActive_stays_true_across_tool_end_to_tool_start_gap_while_run_active()
    {
        // Jon's case: in Sequential mode the runner emits ToolStart(A) -> ToolEnd(A) ->
        // ToolStart(B). Between ToolEnd(A) and ToolStart(B) ActiveToolCalls momentarily empties
        // while IsStreaming is already false. Without the run bracket this flickers to Send.
        var conv = _store.GetAgent("agent-1")!.Conversations["conv-1"];

        _handler.HandleRunStarted(new AgentStreamEvent { SessionId = "sess-1" });
        _handler.HandleMessageStart(new AgentStreamEvent { SessionId = "sess-1" });
        _handler.HandleToolStart(new AgentStreamEvent { SessionId = "sess-1", ToolCallId = "tool-a", ToolName = "read" });
        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });
        _handler.HandleToolEnd(new AgentStreamEvent { SessionId = "sess-1", ToolCallId = "tool-a", ToolName = "read", ToolResult = "ok" });

        // Tool A done, none in flight, not streaming -- the between-tools gap.
        Assert.False(conv.StreamState.IsStreaming);
        Assert.Empty(conv.StreamState.ActiveToolCalls);
        // The run is still active, so controls stay visible across the gap.
        Assert.True(conv.StreamState.IsTurnActive);

        // Next tool starts -- still active, no flicker.
        _handler.HandleToolStart(new AgentStreamEvent { SessionId = "sess-1", ToolCallId = "tool-b", ToolName = "grep" });
        Assert.True(conv.StreamState.IsTurnActive);
    }

    [Fact]
    public void HandleRunEnded_clears_run_active_streaming_and_tools()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];

        _handler.HandleRunStarted(new AgentStreamEvent { SessionId = "sess-1" });
        _handler.HandleMessageStart(new AgentStreamEvent { SessionId = "sess-1" });
        _handler.HandleToolStart(new AgentStreamEvent { SessionId = "sess-1", ToolCallId = "tool-1", ToolName = "read" });
        Assert.True(conv.StreamState.IsTurnActive);

        _handler.HandleRunEnded(new AgentStreamEvent { SessionId = "sess-1" });

        // RunEnded is the only event that should flip the conversation back to idle.
        Assert.False(conv.StreamState.IsRunActive);
        Assert.False(conv.StreamState.IsStreaming);
        Assert.Empty(conv.StreamState.ActiveToolCalls);
        Assert.False(conv.StreamState.IsTurnActive);
        Assert.False(agent.IsStreaming);
        Assert.Null(agent.ProcessingStage);
    }

    [Fact]
    public void Run_stays_active_across_a_full_two_tool_turn_until_run_ended()
    {
        // End-to-end: RunStarted ... (assistant + two sequential tools + a second assistant turn)
        // ... RunEnded. IsTurnActive must be true at every step until RunEnded.
        var conv = _store.GetAgent("agent-1")!.Conversations["conv-1"];

        _handler.HandleRunStarted(new AgentStreamEvent { SessionId = "sess-1" });
        Assert.True(conv.StreamState.IsTurnActive);

        _handler.HandleMessageStart(new AgentStreamEvent { SessionId = "sess-1" });
        _handler.HandleToolStart(new AgentStreamEvent { SessionId = "sess-1", ToolCallId = "t1", ToolName = "read" });
        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });
        Assert.True(conv.StreamState.IsTurnActive);

        _handler.HandleToolEnd(new AgentStreamEvent { SessionId = "sess-1", ToolCallId = "t1", ToolName = "read", ToolResult = "a" });
        Assert.True(conv.StreamState.IsTurnActive); // ToolEnd -> next ToolStart gap
        _handler.HandleToolStart(new AgentStreamEvent { SessionId = "sess-1", ToolCallId = "t2", ToolName = "grep" });
        _handler.HandleToolEnd(new AgentStreamEvent { SessionId = "sess-1", ToolCallId = "t2", ToolName = "grep", ToolResult = "b" });
        Assert.True(conv.StreamState.IsTurnActive); // ToolEnd -> next MessageStart gap

        // Second assistant turn (the model responds to the tool results).
        _handler.HandleMessageStart(new AgentStreamEvent { SessionId = "sess-1" });
        _handler.HandleContentDelta(new AgentStreamEvent { SessionId = "sess-1", ContentDelta = "All done." });
        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });
        Assert.True(conv.StreamState.IsTurnActive); // loop not yet settled

        // Only RunEnded ends it.
        _handler.HandleRunEnded(new AgentStreamEvent { SessionId = "sess-1" });
        Assert.False(conv.StreamState.IsTurnActive);
    }

    [Fact]
    public void HandleError_appends_error_and_clears_buffers()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        agent.IsStreaming = true;
        conv.StreamState.Buffer = "partial";
        conv.StreamState.ThinkingBuffer = "plan";

        _handler.HandleError(new AgentStreamEvent { SessionId = "sess-1", ErrorMessage = "boom" });

        Assert.False(agent.IsStreaming);
        Assert.Equal(string.Empty, conv.StreamState.Buffer);
        Assert.Equal(string.Empty, conv.StreamState.ThinkingBuffer);
        Assert.Equal("Error", conv.Messages.Single().Role);
    }

    [Fact]
    public void HandleMessageEnd_no_reply_sentinel_does_not_add_message_to_conversation()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        agent.IsStreaming = true;
        conv.StreamState.IsStreaming = true;
        // Buffer contains exactly the NO_REPLY sentinel
        conv.StreamState.Buffer = "NO_REPLY";
        conv.StreamState.ThinkingBuffer = "";

        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });

        // Streaming state should be cleared
        Assert.False(agent.IsStreaming);
        Assert.False(conv.StreamState.IsStreaming);
        Assert.Equal(string.Empty, conv.StreamState.Buffer);
        // No message must be added — NO_REPLY is a silent sentinel
        Assert.Empty(conv.Messages);
    }

    [Fact]
    public void HandleMessageEnd_no_reply_sentinel_with_whitespace_does_not_add_message()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        agent.IsStreaming = true;
        conv.StreamState.IsStreaming = true;
        // Gateway may emit the sentinel with surrounding whitespace
        conv.StreamState.Buffer = "  NO_REPLY  ";
        conv.StreamState.ThinkingBuffer = "";

        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });

        Assert.False(agent.IsStreaming);
        Assert.Empty(conv.Messages);
    }

    [Fact]
    public void HandleMessageEnd_non_sentinel_content_is_added_normally()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        agent.IsStreaming = true;
        conv.StreamState.IsStreaming = true;
        conv.StreamState.Buffer = "Hello world";
        conv.StreamState.ThinkingBuffer = "";

        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });

        Assert.Single(conv.Messages);
        Assert.Equal("Hello world", conv.Messages[0].Content);
    }

    [Fact]
    public void HandleSessionReset_preserves_history_and_adds_session_boundary_divider()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        agent.SessionId = "sess-1";
        conv.AppendMessage(new ChatMessage("User", "before reset", DateTimeOffset.UtcNow));
        conv.HistoryLoaded = true;

        _handler.HandleSessionReset(new SessionResetPayload("agent-1", "sess-1"));

        Assert.Null(agent.SessionId);
        Assert.False(agent.IsStreaming);
        // History is preserved — session reset only clears agent context, not visible history
        Assert.True(conv.HistoryLoaded);
        Assert.Equal(2, conv.Messages.Count); // original + divider
        Assert.Equal("User", conv.Messages[0].Role);
        Assert.Equal("before reset", conv.Messages[0].Content);
        Assert.Equal("System", conv.Messages[1].Role);
        Assert.Contains("---", conv.Messages[1].Content); // visual divider
    }

    // ── Fix 3 — Steering feedback ────────────────────────────────────────

    [Fact]
    public void HandleSteeringInjected_AppendsFeedbackMessage()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        var initialCount = conv.Messages.Count;

        _handler.HandleSteeringFeedback(new SteeringFeedbackPayload("agent-1", "sess-1", SteeringFeedbackKind.Injected));

        Assert.Equal(initialCount + 1, conv.Messages.Count);
        var msg = conv.Messages.Last();
        Assert.Equal("System", msg.Role);
        Assert.Contains("↳ Steering injected", msg.Content);
    }

    [Fact]
    public void HandleSteeringQueued_AppendsFeedbackMessage()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        var initialCount = conv.Messages.Count;

        _handler.HandleSteeringFeedback(new SteeringFeedbackPayload("agent-1", "sess-1", SteeringFeedbackKind.Queued));

        Assert.Equal(initialCount + 1, conv.Messages.Count);
        var msg = conv.Messages.Last();
        Assert.Equal("System", msg.Role);
        Assert.Contains("↳ Steering queued", msg.Content);
    }

    [Fact]
    public void HandleCanvasUpdated_updates_canvas_for_matching_conversation()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        conv.CanvasHtml = null;

        _handler.HandleCanvasUpdated("agent-1", "conv-1", "<div>Canvas</div>");

        Assert.Equal("<div>Canvas</div>", conv.CanvasHtml);
        Assert.NotNull(conv.CanvasUpdatedAt);
    }

    [Fact]
    public void HandleCanvasUpdated_ignores_unknown_agent()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        conv.CanvasHtml = "<p>existing</p>";

        _handler.HandleCanvasUpdated("missing-agent", "conv-1", "<div>new</div>");

        Assert.Equal("<p>existing</p>", conv.CanvasHtml);
    }

    [Fact]
    public void HandleSubAgentCompleted_routes_completion_to_originating_conversation_when_active_switches()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-2"] = new ConversationState
        {
            ConversationId = "conv-2",
            Title = "Conversation 2",
            ActiveSessionId = "sess-2"
        };
        _store.RegisterSession("agent-1", "sess-2");

        _store.SetActiveConversation("agent-1", "conv-1");
        _handler.HandleSubAgentSpawned(new SubAgentEventPayload(
            SessionId: "sess-1",
            SubAgentId: "sub-1",
            Name: "Hermes worker",
            Task: "Run checks",
            Model: "gpt-5-mini",
            Archetype: "general-purpose",
            Status: "Running",
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: null,
            TurnsUsed: 0,
            ResultSummary: null,
            TimedOut: false,
            ChildSessionId: null));

        _store.SetActiveConversation("agent-1", "conv-2");

        _handler.HandleSubAgentCompleted(new SubAgentEventPayload(
            SessionId: "sess-1",
            SubAgentId: "sub-1",
            Name: "Hermes worker",
            Task: "Run checks",
            Model: "gpt-5-mini",
            Archetype: "general-purpose",
            Status: "Completed",
            StartedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt: DateTimeOffset.UtcNow,
            TurnsUsed: 1,
            ResultSummary: "All green",
            TimedOut: false,
            ChildSessionId: null));

        var conversationOneMessages = agent.Conversations["conv-1"].Messages.Select(m => m.Content).ToList();
        var conversationTwoMessages = agent.Conversations["conv-2"].Messages.Select(m => m.Content).ToList();

        Assert.Contains(conversationOneMessages, content => content.Contains("Sub-agent completed", StringComparison.Ordinal));
        Assert.DoesNotContain(conversationTwoMessages, content => content.Contains("Sub-agent completed", StringComparison.Ordinal));
    }

    [Fact]
    public void HandleSubAgentFailed_routes_failure_to_originating_conversation_when_active_switches()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-2"] = new ConversationState
        {
            ConversationId = "conv-2",
            Title = "Conversation 2",
            ActiveSessionId = "sess-2"
        };
        _store.RegisterSession("agent-1", "sess-2");

        _store.SetActiveConversation("agent-1", "conv-1");
        _handler.HandleSubAgentSpawned(new SubAgentEventPayload(
            SessionId: "sess-1",
            SubAgentId: "sub-2",
            Name: "Hermes worker",
            Task: "Run checks",
            Model: "gpt-5-mini",
            Archetype: "general-purpose",
            Status: "Running",
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: null,
            TurnsUsed: 0,
            ResultSummary: null,
            TimedOut: false,
            ChildSessionId: null));

        _store.SetActiveConversation("agent-1", "conv-2");

        _handler.HandleSubAgentFailed(new SubAgentEventPayload(
            SessionId: "sess-1",
            SubAgentId: "sub-2",
            Name: "Hermes worker",
            Task: "Run checks",
            Model: "gpt-5-mini",
            Archetype: "general-purpose",
            Status: "Failed",
            StartedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt: DateTimeOffset.UtcNow,
            TurnsUsed: 1,
            ResultSummary: "Tool call failed",
            TimedOut: false,
            ChildSessionId: null));

        var conversationOneMessages = agent.Conversations["conv-1"].Messages.Select(m => m.Content).ToList();
        var conversationTwoMessages = agent.Conversations["conv-2"].Messages.Select(m => m.Content).ToList();

        Assert.Contains(conversationOneMessages, content => content.Contains("Sub-agent failed", StringComparison.Ordinal));
        Assert.DoesNotContain(conversationTwoMessages, content => content.Contains("Sub-agent failed", StringComparison.Ordinal));
    }

    [Fact]
    public void HandleSubAgentKilled_routes_killed_notice_to_originating_conversation_when_active_switches()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-2"] = new ConversationState
        {
            ConversationId = "conv-2",
            Title = "Conversation 2",
            ActiveSessionId = "sess-2"
        };
        _store.RegisterSession("agent-1", "sess-2");

        _store.SetActiveConversation("agent-1", "conv-1");
        _handler.HandleSubAgentSpawned(new SubAgentEventPayload(
            SessionId: "sess-1",
            SubAgentId: "sub-3",
            Name: "Hermes worker",
            Task: "Run checks",
            Model: "gpt-5-mini",
            Archetype: "general-purpose",
            Status: "Running",
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: null,
            TurnsUsed: 0,
            ResultSummary: null,
            TimedOut: false,
            ChildSessionId: null));

        _store.SetActiveConversation("agent-1", "conv-2");

        _handler.HandleSubAgentKilled(new SubAgentEventPayload(
            SessionId: "sess-1",
            SubAgentId: "sub-3",
            Name: "Hermes worker",
            Task: "Run checks",
            Model: "gpt-5-mini",
            Archetype: "general-purpose",
            Status: "Killed",
            StartedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt: DateTimeOffset.UtcNow,
            TurnsUsed: 1,
            ResultSummary: null,
            TimedOut: false,
            ChildSessionId: null));

        var conversationOneMessages = agent.Conversations["conv-1"].Messages.Select(m => m.Content).ToList();
        var conversationTwoMessages = agent.Conversations["conv-2"].Messages.Select(m => m.Content).ToList();

        Assert.Contains(conversationOneMessages, content => content.Contains("Sub-agent killed", StringComparison.Ordinal));
        Assert.DoesNotContain(conversationTwoMessages, content => content.Contains("Sub-agent killed", StringComparison.Ordinal));
    }

    [Fact]
    public void HandleCanvasUpdated_empty_html_clears_canvas_state()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        conv.CanvasHtml = "<html><body>existing</body></html>";

        _handler.HandleCanvasUpdated("agent-1", "conv-1", " ");

        Assert.Null(conv.CanvasHtml);
    }

    // -- Fix #235 - Sub-agent spawn notification task truncation -----------

    [Fact]
    public void HandleSubAgentSpawned_short_task_is_included_verbatim_in_notification()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        var shortTask = "Run the build checks";

        _handler.HandleSubAgentSpawned(new SubAgentEventPayload(
            SessionId: "sess-1",
            SubAgentId: "sub-short",
            Name: "Builder",
            Task: shortTask,
            Model: null,
            Archetype: "general",
            Status: "Running",
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: null,
            TurnsUsed: 0,
            ResultSummary: null,
            TimedOut: false,
            ChildSessionId: null));

        var msg = conv.Messages.Last();
        Assert.Equal("System", msg.Role);
        Assert.Contains("Builder", msg.Content, StringComparison.Ordinal);
        Assert.Contains(shortTask, msg.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void HandleSubAgentSpawned_empty_task_omits_separator_from_notification()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];

        _handler.HandleSubAgentSpawned(new SubAgentEventPayload(
            SessionId: "sess-1",
            SubAgentId: "sub-notask",
            Name: "Silent worker",
            Task: "",
            Model: null,
            Archetype: "general",
            Status: "Running",
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: null,
            TurnsUsed: 0,
            ResultSummary: null,
            TimedOut: false,
            ChildSessionId: null));

        var msg = conv.Messages.Last();
        Assert.Equal("System", msg.Role);
        Assert.Contains("Silent worker", msg.Content, StringComparison.Ordinal);
        // No em dash separator when task is empty
        Assert.DoesNotContain(" \u2014 ", msg.Content, StringComparison.Ordinal);
    }

    // -- Fix #314 - Agent switch routes to correct conversation --

    [Fact]
    public void HandleMessageStart_AfterAgentSwitch_RoutesToNewActiveConversation()
    {
        // Arrange: agent has two conversations, conv-2 is newly active but has no ActiveSessionId yet
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-2"] = new ConversationState
        {
            ConversationId = "conv-2",
            Title = "New Conversation",
            ActiveSessionId = null   // not yet set before REST refresh
        };
        _store.SetActiveConversation("agent-1", "conv-2");
        // Simulate RegisterSession being called after SendMessageAsync returns sess-2
        _store.RegisterSession("agent-1", "sess-2");

        // Act: streaming event arrives for sess-2 before REST refresh completes
        _handler.HandleMessageStart(new AgentStreamEvent { SessionId = "sess-2" });

        // Assert: streaming state is on conv-2, NOT on conv-1
        var conv2 = agent.Conversations["conv-2"];
        Assert.True(conv2.StreamState.IsStreaming, "conv-2 should be streaming");
        var conv1 = agent.Conversations["conv-1"];
        Assert.False(conv1.StreamState.IsStreaming, "conv-1 should NOT be streaming after agent switch");
    }

    [Fact]
    public void RegisterSession_ImmediatelySetsActiveConversationSessionId()
    {
        // When RegisterSession is called after SendMessageAsync, the active conversation's
        // ActiveSessionId should be updated immediately so TryResolveConversationBySession works
        // without waiting for the async REST refresh (race condition fix for #314).
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-2"] = new ConversationState
        {
            ConversationId = "conv-2",
            Title = "New Conversation",
            ActiveSessionId = null
        };
        _store.SetActiveConversation("agent-1", "conv-2");

        _store.RegisterSession("agent-1", "sess-2");

        // TryResolveConversationBySession should now directly resolve conv-2 via ActiveSessionId
        var found = _store.TryResolveConversationBySession("agent-1", "sess-2", out var resolvedConvId);
        Assert.True(found, "session should be directly resolved to conv-2 after RegisterSession");
        Assert.Equal("conv-2", resolvedConvId);
    }

    // -- Fix #456 - Deferred conversation refresh during streaming --------

    [Fact]
    public void HandleConversationChanged_DefersRefresh_WhenAgentIsStreaming()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.IsStreaming = true;

        var refreshCalled = false;
        _handler.ConversationRefreshDelegate = _ => { refreshCalled = true; return Task.CompletedTask; };

        // A new conversation arrives that is not in local state (fast path misses)
        _handler.HandleConversationChanged(new ConversationChangedPayload("create", "agent-1", "new-conv-99"));

        // Must NOT refresh while streaming
        Assert.False(refreshCalled, "refresh must be deferred during active stream");
    }

    [Fact]
    public async Task HandleConversationChanged_DrainsPendingRefresh_AfterMessageEnd()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        agent.IsStreaming = true;
        conv.StreamState.IsStreaming = true;
        conv.StreamState.Buffer = "hello";

        var refreshCount = 0;
        _handler.ConversationRefreshDelegate = _ => { refreshCount++; return Task.CompletedTask; };

        // Conversation change arrives mid-stream
        _handler.HandleConversationChanged(new ConversationChangedPayload("create", "agent-1", "new-conv-99"));
        Assert.Equal(0, refreshCount); // deferred

        // Stream ends
        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });

        await Task.Delay(20); // allow fire-and-forget refresh to execute
        Assert.Equal(1, refreshCount); // drained after turn end
    }

    [Fact]
    public void HandleConversationChanged_RefreshesImmediately_WhenNotStreaming()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.IsStreaming = false;

        var refreshCalled = false;
        _handler.ConversationRefreshDelegate = _ => { refreshCalled = true; return Task.CompletedTask; };

        _handler.HandleConversationChanged(new ConversationChangedPayload("create", "agent-1", "new-conv-99"));

        Assert.True(refreshCalled, "refresh must fire immediately when not streaming");
    }


    // ---- NO_REPLY sentinel tests (issue #773) ----

    [Fact]
    public void HandleMessageEnd_noReply_does_not_add_message_to_conversation()
    {
        // NO_REPLY turns must leave conversation.Messages untouched.
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        agent.IsStreaming = true;
        conv.StreamState.IsStreaming = true;
        conv.StreamState.Buffer = "NO_REPLY";

        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });

        Assert.Empty(conv.Messages);
    }

    [Fact]
    public void HandleMessageEnd_noReply_does_not_update_conversation_timestamp()
    {
        // NO_REPLY turns must not bump UpdatedAt -- bumping it surfaces the conversation
        // as recently-active in the sidebar and creates noise for cron no-ops (#773).
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        var originalUpdatedAt = conv.UpdatedAt;
        conv.StreamState.Buffer = "NO_REPLY";

        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });

        Assert.Equal(originalUpdatedAt, conv.UpdatedAt);
    }

    [Fact]
    public void HandleMessageEnd_noReply_does_not_increment_unread_counts()
    {
        // NO_REPLY turns must not increment agent or conversation unread counts.
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        var agentUnreadBefore = agent.UnreadCount;
        var convUnreadBefore = conv.UnreadCount;
        conv.StreamState.Buffer = "NO_REPLY";

        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });

        Assert.Equal(agentUnreadBefore, agent.UnreadCount);
        Assert.Equal(convUnreadBefore, conv.UnreadCount);
    }

    [Fact]
    public void HandleMessageEnd_noReply_still_clears_streaming_state()
    {
        // Even for NO_REPLY, streaming flags must be cleared so the UI does not
        // show a perpetual "Agent is responding..." indicator.
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        agent.IsStreaming = true;
        conv.StreamState.IsStreaming = true;
        conv.StreamState.Buffer = "NO_REPLY";

        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });

        Assert.False(agent.IsStreaming);
        Assert.False(conv.StreamState.IsStreaming);
        Assert.Equal(string.Empty, conv.StreamState.Buffer);
        Assert.Null(agent.ProcessingStage);
    }

    [Fact]
    public void HandleMessageEnd_noReply_with_leading_trailing_whitespace_is_suppressed()
    {
        // The sentinel check uses .Trim() so whitespace around NO_REPLY is tolerated.
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        var originalUpdatedAt = conv.UpdatedAt;
        conv.StreamState.Buffer = "  NO_REPLY  ";

        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });

        Assert.Empty(conv.Messages);
        Assert.Equal(originalUpdatedAt, conv.UpdatedAt);
    }


    // #668 - TurnEnd: portal should clear IsStreaming on tool-only turns
    // that emit no MessageEnd.

    [Fact]
    public void HandleTurnEnd_WhenStillStreaming_ClearsStreamingState()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];

        // Simulate a tool-only turn in progress.
        agent.IsStreaming = true;
        agent.ProcessingStage = "Running tools";
        conv.StreamState.IsStreaming = true;
        conv.StreamState.Buffer = "partial";
        conv.StreamState.ThinkingBuffer = "thinking";

        _handler.HandleTurnEnd(new AgentStreamEvent { SessionId = "sess-1", ConversationId = "conv-1" });

        Assert.False(agent.IsStreaming);
        Assert.Null(agent.ProcessingStage);
        Assert.False(conv.StreamState.IsStreaming);
        Assert.Equal("", conv.StreamState.Buffer);
        Assert.Equal("", conv.StreamState.ThinkingBuffer);
    }

    [Fact]
    public void HandleTurnEnd_WhenNotStreaming_IsNoOp()
    {
        // If MessageEnd already cleared IsStreaming, HandleTurnEnd should be a no-op.
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        agent.IsStreaming = false;
        conv.StreamState.IsStreaming = false;
        var msgCountBefore = conv.Messages.Count;

        _handler.HandleTurnEnd(new AgentStreamEvent { SessionId = "sess-1", ConversationId = "conv-1" });

        Assert.False(agent.IsStreaming);
        Assert.Equal(msgCountBefore, conv.Messages.Count); // no phantom message added
    }

    [Fact]
    public void HandleTurnEnd_ToolOnlyTurn_DoesNotAddVisibleMessage()
    {
        // Tool-only turns (e.g. cron NO_REPLY) should NOT add any user-visible message.
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        agent.IsStreaming = true;
        conv.StreamState.IsStreaming = true;
        conv.StreamState.Buffer = "NO_REPLY";
        var msgCountBefore = conv.Messages.Count;

        _handler.HandleTurnEnd(new AgentStreamEvent { SessionId = "sess-1", ConversationId = "conv-1" });

        // Streaming cleared, no new message added.
        Assert.False(agent.IsStreaming);
        Assert.Equal(msgCountBefore, conv.Messages.Count);
    }


    // ---- Stream/history reconciliation tests (issue #759) ----

    [Fact]
    public async Task HandleReconnectedAsync_marks_HistoryLoaded_false_for_streaming_conversations()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        conv.HistoryLoaded = true;
        agent.IsStreaming = true;
        conv.StreamState.IsStreaming = true;
        conv.StreamState.Buffer = "partial response";

        // Simulate disconnect while streaming
        _handler.HandleReconnecting();

        // Now reconnect
        await _handler.HandleReconnectedAsync();

        // History should be marked for reload so the UI fetches server state
        Assert.False(conv.HistoryLoaded);
        // Stream state should be cleared
        Assert.False(conv.StreamState.IsStreaming);
        Assert.Equal(string.Empty, conv.StreamState.Buffer);
    }

    [Fact]
    public async Task HandleReconnectedAsync_does_not_mark_HistoryLoaded_false_for_non_streaming_agents()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        conv.HistoryLoaded = true;
        agent.IsStreaming = false;

        // Simulate disconnect while NOT streaming
        _handler.HandleReconnecting();

        // Reconnect
        await _handler.HandleReconnectedAsync();

        // History should remain loaded (no stream was lost)
        Assert.True(conv.HistoryLoaded);
    }

    [Fact]
    public void HandleConversationChanged_defers_refresh_when_streaming_then_drains_on_MessageEnd()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];

        // Start streaming
        _handler.HandleMessageStart(new AgentStreamEvent { SessionId = "sess-1" });

        // Simulate a conversation change event mid-stream
        var refreshCalled = false;
        _handler.ConversationRefreshDelegate = _ => { refreshCalled = true; return Task.CompletedTask; };
        _handler.HandleConversationChanged(new ConversationChangedPayload("update", "agent-1", "conv-1"));
        Assert.False(refreshCalled, "refresh must be deferred while streaming");

        // End the stream
        conv.StreamState.Buffer = "response text";
        _handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1" });

        // Now the deferred refresh should have fired
        Assert.True(refreshCalled, "deferred refresh must fire after MessageEnd");
        // And the message should still be in the list (not lost)
        Assert.Contains(conv.Messages, m => m.Content == "response text");
    }

    // ---- StreamState.Reset() single-enforcement-point tests (issue #1390 / #456 / #668 / #759) ----

    [Fact]
    public void ConversationStreamState_Reset_clears_all_three_stream_fields()
    {
        // The reset must atomically clear Buffer, ThinkingBuffer, and IsStreaming so a
        // terminal handler can never leave the portal stuck in a perpetual streaming
        // indicator by forgetting one field (the recurring #456/#668/#759 bug class).
        var state = new ConversationStreamState
        {
            IsStreaming = true,
            Buffer = "partial",
            ThinkingBuffer = "plan"
        };

        state.Reset();

        Assert.False(state.IsStreaming);
        Assert.Equal(string.Empty, state.Buffer);
        Assert.Equal(string.Empty, state.ThinkingBuffer);
    }

    [Fact]
    public void ConversationStreamState_Reset_preserves_active_tool_calls()
    {
        // Reset clears the streaming buffers but must NOT drop in-flight tool calls --
        // IsTurnActive has to stay true while tools run between LLM generations.
        var state = new ConversationStreamState { IsStreaming = true };
        state.ActiveToolCalls["tool-1"] = new ActiveToolCall
        {
            ToolCallId = "tool-1",
            ToolName = "search",
            StartedAt = DateTimeOffset.UtcNow,
            MessageId = "msg-1"
        };

        state.Reset();

        Assert.False(state.IsStreaming);
        Assert.Single(state.ActiveToolCalls);
        Assert.True(state.IsTurnActive); // still active because a tool call remains
    }

    [Fact]
    public void HandleTurnInterrupted_clears_streaming_state_via_reset()
    {
        // A gateway restart mid-turn must clear the streaming indicator (the terminal-handler
        // invariant now enforced through the single StreamState.Reset() call).
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        agent.IsStreaming = true;
        agent.ProcessingStage = "Generating";
        conv.StreamState.IsStreaming = true;
        conv.StreamState.Buffer = "partial";
        conv.StreamState.ThinkingBuffer = "thinking";

        _handler.HandleTurnInterrupted(new AgentStreamEvent { SessionId = "sess-1", ConversationId = "conv-1" });

        Assert.False(agent.IsStreaming);
        Assert.Null(agent.ProcessingStage);
        Assert.False(conv.StreamState.IsStreaming);
        Assert.Equal(string.Empty, conv.StreamState.Buffer);
        Assert.Equal(string.Empty, conv.StreamState.ThinkingBuffer);
        // A notification message about the interruption is surfaced to the user.
        Assert.Contains(conv.Messages, m => m.Role == "Notification");
    }

    // ---- #2195: resilient RunEnded recovery when the event is misrouted ----

    [Fact]
    public void HandleRunEnded_with_mismatched_ConversationId_still_clears_active_conversation_bracket()
    {
        // #2195: A RunEnded whose ConversationId points at a conversation the client does not
        // know (misrouted/stale hint) must NOT leave the active conversation stuck turn-active.
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];

        _handler.HandleRunStarted(new AgentStreamEvent { SessionId = "sess-1" });
        _handler.HandleMessageStart(new AgentStreamEvent { SessionId = "sess-1" });
        _handler.HandleToolStart(new AgentStreamEvent { SessionId = "sess-1", ToolCallId = "tool-1", ToolName = "read" });
        Assert.True(conv.StreamState.IsTurnActive);

        // RunEnded arrives carrying a ConversationId that does not resolve to any local conversation.
        _handler.HandleRunEnded(new AgentStreamEvent { SessionId = "sess-1", ConversationId = "conv-does-not-exist" });

        Assert.False(conv.StreamState.IsRunActive);
        Assert.False(conv.StreamState.IsStreaming);
        Assert.Empty(conv.StreamState.ActiveToolCalls);
        Assert.False(conv.StreamState.IsTurnActive);
        Assert.False(agent.IsStreaming);
        Assert.Null(agent.ProcessingStage);
    }

    [Fact]
    public void HandleRunEnded_with_null_ConversationId_and_unregistered_session_clears_active_bracket()
    {
        // #2195: the RunEnded may carry no ConversationId hint. As long as the owning agent can be
        // resolved, the agent's active conversation bracket must still be cleared so the input is
        // not stuck on turn-active controls until reload.
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];

        _handler.HandleRunStarted(new AgentStreamEvent { SessionId = "sess-1" });
        _handler.HandleMessageStart(new AgentStreamEvent { SessionId = "sess-1" });
        Assert.True(conv.StreamState.IsTurnActive);

        _handler.HandleRunEnded(new AgentStreamEvent { SessionId = "sess-1", ConversationId = null });

        Assert.False(conv.StreamState.IsTurnActive);
        Assert.False(agent.IsStreaming);
    }
}

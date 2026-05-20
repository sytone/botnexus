using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class GatewayEventHandlerTests
{
    private readonly ClientStateStore _store = new();
    private readonly GatewayEventHandler _handler;

    public GatewayEventHandlerTests()
    {
        _handler = new GatewayEventHandler(_store, new GatewayHubConnection());

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
        conv.Messages.Add(new ChatMessage("User", "before reset", DateTimeOffset.UtcNow));
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
        Assert.Contains("───", conv.Messages[1].Content); // visual divider
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
        Assert.Contains("↳ Steering accepted mid-turn", msg.Content);
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
    public void HandleCanvasUpdated_updates_canvas_for_matching_agent()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.CanvasHtml = null;

        _handler.HandleCanvasUpdated("agent-1", "<div>Canvas</div>");

        Assert.Equal("<div>Canvas</div>", agent.CanvasHtml);
        Assert.NotNull(agent.CanvasUpdatedAt);
    }

    [Fact]
    public void HandleCanvasUpdated_ignores_unknown_agent()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.CanvasHtml = "<p>existing</p>";

        _handler.HandleCanvasUpdated("missing-agent", "<div>new</div>");

        Assert.Equal("<p>existing</p>", agent.CanvasHtml);
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
        agent.CanvasHtml = "<html><body>existing</body></html>";

        _handler.HandleCanvasUpdated("agent-1", " ");

        Assert.Null(agent.CanvasHtml);
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
}
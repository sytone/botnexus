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
}

using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ChatPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store;
    private readonly IAgentInteractionService _interaction;

    public ChatPanelTests()
    {
        _store = new ClientStateStore();
        _interaction = Substitute.For<IAgentInteractionService>();

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private AgentState CreateAndSeedAgent(string agentId, string displayName = "Test Agent",
        bool isConnected = false, bool isStreaming = false)
    {
        var agent = new AgentState
        {
            AgentId = agentId,
            DisplayName = displayName,
            IsConnected = isConnected,
            IsStreaming = isStreaming
        };
        _store.UpsertAgent(agent);
        return agent;
    }

    private static ConversationSummaryDto MakeConvDto(string convId, string agentId, string title = "Test Conv", bool isDefault = false) =>
        new ConversationSummaryDto(
            ConversationId: convId,
            AgentId: agentId,
            Title: title,
            IsDefault: isDefault,
            Status: "Active",
            ActiveSessionId: null,
            BindingCount: 0,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public void Renders_agent_display_name_in_header()
    {
        CreateAndSeedAgent("agent-1", "My Assistant");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Contains("My Assistant", cut.Markup);
    }

    [Fact]
    public void Renders_agent_id_label_in_header()
    {
        CreateAndSeedAgent("agent-xyz");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-xyz"));

        Assert.Contains("agent-xyz", cut.Markup);
    }

    [Fact]
    public void New_session_button_is_present_in_header()
    {
        CreateAndSeedAgent("agent-1");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        var btn = cut.Find(".new-chat-btn");
        Assert.NotNull(btn);
    }

    [Fact]
    public void New_session_button_is_disabled_while_streaming()
    {
        CreateAndSeedAgent("agent-1", isStreaming: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.SetStreaming("conv-1", true); // IsStreaming now reads per-conversation

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        var btn = cut.Find(".new-chat-btn");
        Assert.True(btn.HasAttribute("disabled"));
    }

    [Fact]
    public void New_session_button_is_enabled_when_not_streaming()
    {
        CreateAndSeedAgent("agent-1", isStreaming: false);

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        var btn = cut.Find(".new-chat-btn");
        Assert.False(btn.HasAttribute("disabled"));
    }

    [Fact]
    public void Shows_streaming_badge_when_agent_is_streaming()
    {
        var agent = CreateAndSeedAgent("agent-1", isStreaming: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.SetStreaming("conv-1", true); // IsStreaming now reads per-conversation

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        cut.Find(".streaming-badge");
    }

    [Fact]
    public void Does_not_show_streaming_badge_when_not_streaming()
    {
        CreateAndSeedAgent("agent-1", isStreaming: false);

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Empty(cut.FindAll(".streaming-badge"));
    }

    [Fact]
    public void Shows_empty_state_when_no_active_conversation()
    {
        CreateAndSeedAgent("agent-1");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Contains("Select a conversation", cut.Markup);
    }

    [Fact]
    public void Renders_user_message_with_user_css_class()
    {
        CreateAndSeedAgent("agent-1", isConnected: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("User", "Hello!", DateTimeOffset.UtcNow));

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        var userMsgs = cut.FindAll(".message.user");
        Assert.NotEmpty(userMsgs);
    }

    [Fact]
    public void Renders_user_message_content()
    {
        CreateAndSeedAgent("agent-1", isConnected: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("User", "Hello from user!", DateTimeOffset.UtcNow));

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Contains("Hello from user!", cut.Markup);
    }

    [Fact]
    public void Renders_assistant_message_with_assistant_css_class()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Assistant", "Hello!", DateTimeOffset.UtcNow));

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        var msgs = cut.FindAll(".message.assistant");
        Assert.NotEmpty(msgs);
    }

    [Fact]
    public void Renders_tool_call_with_tool_name()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Tool", "", DateTimeOffset.UtcNow)
        {
            IsToolCall = true,
            ToolName = "search_files",
            ToolResult = "found 3 files"
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Contains("search_files", cut.Markup);
    }

    [Fact]
    public void Renders_pending_tool_hourglass_when_result_is_null()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Tool", "", DateTimeOffset.UtcNow)
        {
            IsToolCall = true,
            ToolName = "some_tool",
            ToolResult = null
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Contains("⏳", cut.Markup);
    }

    [Fact]
    public void Renders_checkmark_when_tool_result_is_set()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Tool", "", DateTimeOffset.UtcNow)
        {
            IsToolCall = true,
            ToolName = "done_tool",
            ToolResult = "success",
            ToolIsError = false
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Contains("✅", cut.Markup);
    }

    [Fact]
    public void Renders_error_icon_when_tool_is_error()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Tool", "", DateTimeOffset.UtcNow)
        {
            IsToolCall = true,
            ToolName = "failing_tool",
            ToolResult = "error details",
            ToolIsError = true
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Contains("❌", cut.Markup);
        Assert.Contains("tool-error", cut.Markup);
    }

    [Fact]
    public void Renders_session_boundary_divider_with_label()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("system", "", DateTimeOffset.UtcNow)
        {
            Kind = "boundary",
            BoundaryLabel = "Session started"
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        cut.Find(".session-boundary");
        Assert.Contains("Session started", cut.Markup);
    }

    [Fact]
    public void Renders_conversation_title_in_header_when_active_conversation_is_set()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1", title: "My Important Conversation")]);
        _store.SetActiveConversation("agent-1", "conv-1");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Contains("My Important Conversation", cut.Markup);
    }
    [Fact]
    public void ChatPanel_SubscribesToStoreOnChanged_RendersOnNotify()
    {
        // ChatPanel must subscribe to Store.OnChanged so streaming responses
        // trigger re-renders without user interaction.
        var store = new ClientStateStore();
        store.SeedAgents([new AgentSummary("a", "Agent A")]);
        _ctx.Services.AddSingleton<IClientStateStore>(store);
        _ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _ctx.Services.AddSingleton(Substitute.For<IPortalLoadService>());

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "a"));
        var initialRenderCount = cut.RenderCount;

        // Simulate GatewayEventHandler pushing a change
        store.NotifyChanged();

        // Component should re-render if subscribed
        cut.RenderCount.ShouldBeGreaterThan(initialRenderCount);
    }

    [Fact]
    public void ChatPanel_UnsubscribesOnDispose_NoLeaks()
    {
        // Verify unsubscribe on dispose prevents memory leaks / dead renders
        var store = new ClientStateStore();
        store.SeedAgents([new AgentSummary("a", "Agent A")]);
        _ctx.Services.AddSingleton<IClientStateStore>(store);
        _ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _ctx.Services.AddSingleton(Substitute.For<IPortalLoadService>());

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "a"));
        cut.Dispose();

        var ex = Record.Exception(() => store.NotifyChanged());
        ex.ShouldBeNull();
    }

}
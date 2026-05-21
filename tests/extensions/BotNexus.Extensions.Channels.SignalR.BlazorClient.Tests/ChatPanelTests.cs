using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
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
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());
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
    public void Agent_id_label_is_not_rendered_in_header()
    {
        // The agent ID label was removed from the chat canvas header (#292)
        // as it is redundant with the top-level agent control.
        CreateAndSeedAgent("agent-xyz");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-xyz"));

        Assert.DoesNotContain("agent-id-label", cut.Markup);
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
    public void Read_only_sub_agent_view_hides_interactive_controls()
    {
        CreateAndSeedAgent("sub-1", "Sub Agent", isConnected: true);
        _store.SeedConversations("sub-1", [MakeConvDto("subagent-session:sub-1", "sub-1", "Sub-agent session")]);
        _store.SetActiveConversation("sub-1", "subagent-session:sub-1");

        var agent = _store.GetAgent("sub-1")!;
        agent.SessionType = "agent-subagent";
        var conversation = agent.Conversations["subagent-session:sub-1"];
        conversation.IsVirtualSession = true;
        conversation.VirtualSessionKind = "subagent";

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "sub-1"));

        cut.Find(".read-only-banner");
        Assert.Empty(cut.FindAll(".chat-input"));
        Assert.Empty(cut.FindAll(".new-chat-btn"));
        Assert.Empty(cut.FindAll(".conversation-title.editable"));
    }

    [Fact]
    public void Stale_active_conversation_id_without_backing_conversation_shows_empty_state()
    {
        var agent = CreateAndSeedAgent("agent-1");
        agent.ActiveConversationId = "missing-conversation";

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Contains("Select a conversation", cut.Markup);
    }

    [Fact]
    public void Stale_removed_cron_conversation_shows_empty_state_after_refresh()
    {
        var agent = CreateAndSeedAgent("agent-1");
        agent.ActiveConversationId = "cron-session:removed-cron-session";

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Contains("Select a conversation", cut.Markup);
    }

    [Fact]
    public void First_render_binds_prevent_enter_with_non_empty_element_reference()
    {
        CreateAndSeedAgent("agent-1");
        _ctx.JSInterop.Mode = JSRuntimeMode.Strict;
        _ctx.JSInterop.SetupVoid("chatScroll.preventEnterSubmit", _ => true);
        _ctx.JSInterop.SetupVoid("BotNexus.attachCodeCopyButtons", _ => true);
        _ctx.JSInterop.SetupVoid("chatScroll.forceScrollToBottom", _ => true);

        _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        var invocation = Assert.Single(_ctx.JSInterop.Invocations,
            i => i.Identifier == "chatScroll.preventEnterSubmit");
        var arg = Assert.IsType<ElementReference>(Assert.Single(invocation.Arguments));
        Assert.False(string.IsNullOrWhiteSpace(arg.Id));
    }

    [Fact]
    public void Read_only_sub_agent_view_does_not_bind_prevent_enter_submit()
    {
        CreateAndSeedAgent("sub-1", "Sub Agent", isConnected: true);
        _store.SeedConversations("sub-1", [MakeConvDto("subagent-session:sub-1", "sub-1", "Sub-agent session")]);
        _store.SetActiveConversation("sub-1", "subagent-session:sub-1");

        var agent = _store.GetAgent("sub-1")!;
        agent.SessionType = "agent-subagent";
        var conversation = agent.Conversations["subagent-session:sub-1"];
        conversation.IsVirtualSession = true;
        conversation.VirtualSessionKind = "subagent";

        _ctx.JSInterop.Mode = JSRuntimeMode.Strict;
        _ctx.JSInterop.SetupVoid("BotNexus.attachCodeCopyButtons", _ => true);
        _ctx.JSInterop.SetupVoid("chatScroll.forceScrollToBottom", _ => true);

        _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "sub-1"));

        Assert.DoesNotContain(_ctx.JSInterop.Invocations,
            i => i.Identifier == "chatScroll.preventEnterSubmit");
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
    public void Renders_copy_button_for_completed_assistant_messages()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Assistant", "Hello from assistant", DateTimeOffset.UtcNow));

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Single(cut.FindAll(".msg-copy-btn"));
    }

    [Fact]
    public void Does_not_render_copy_button_for_user_messages()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("User", "Hello from user", DateTimeOffset.UtcNow));

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Empty(cut.FindAll(".msg-copy-btn"));
    }

    [Fact]
    public void Does_not_render_copy_button_for_streaming_assistant_message()
    {
        CreateAndSeedAgent("agent-1", isStreaming: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.SetStreaming("conv-1", true);
        _store.AppendStreamBuffer("conv-1", "streaming text");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        var streamingMessage = cut.Find(".message.assistant.streaming");
        Assert.Empty(streamingMessage.QuerySelectorAll(".msg-copy-btn"));
    }

    [Fact]
    public void Clicking_assistant_copy_button_invokes_clipboard_copy_with_raw_message_content()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Assistant", "```csharp\nConsole.WriteLine(42);\n```", DateTimeOffset.UtcNow));

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        cut.Find(".msg-copy-btn").Click();

        Assert.Contains(_ctx.JSInterop.Invocations, invocation =>
            invocation.Identifier == "BotNexus.copyToClipboard" &&
            invocation.Arguments.Count == 1 &&
            invocation.Arguments[0] is string copiedText &&
            copiedText == "```csharp\nConsole.WriteLine(42);\n```");
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
    public void Adds_expanded_class_only_after_tool_details_are_opened()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Tool", "", DateTimeOffset.UtcNow)
        {
            IsToolCall = true,
            ToolName = "search_files",
            ToolArgs = "{\"query\":\"tool rows\"}",
            ToolResult = "found 3 files"
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        void AssertExpandedState(bool shouldBeExpanded)
        {
            var toolMessage = cut.Find(".message.tool");
            if (shouldBeExpanded)
            {
                Assert.Contains("expanded", toolMessage.ClassList);
            }
            else
            {
                Assert.DoesNotContain("expanded", toolMessage.ClassList);
            }
        }

        AssertExpandedState(false);

        cut.Find(".tool-header").Click();

        AssertExpandedState(true);

        cut.Find(".tool-header").Click();

        AssertExpandedState(false);
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
    public void Renders_copy_buttons_in_expanded_tool_sections()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Tool", "", DateTimeOffset.UtcNow)
        {
            IsToolCall = true,
            ToolName = "search_files",
            ToolArgs = "{\"query\":\"test\"}",
            ToolResult = "found 3 files"
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        // Not visible before expanding
        Assert.Empty(cut.FindAll(".tool-copy-btn"));

        // Expand tool details
        cut.Find(".tool-header").Click();

        // Should have two copy buttons (Arguments + Result)
        Assert.Equal(2, cut.FindAll(".tool-copy-btn").Count);
    }

    [Fact]
    public void Tool_copy_button_renders_only_for_args_when_result_is_null()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Tool", "", DateTimeOffset.UtcNow)
        {
            IsToolCall = true,
            ToolName = "pending_tool",
            ToolArgs = "{\"path\":\"file.cs\"}",
            ToolResult = null
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));
        cut.Find(".tool-header").Click();

        // Only the Arguments copy button should render
        Assert.Single(cut.FindAll(".tool-copy-btn"));
    }

    [Fact]
    public void Clicking_tool_args_copy_button_invokes_clipboard_with_raw_args()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Tool", "", DateTimeOffset.UtcNow)
        {
            IsToolCall = true,
            ToolName = "search_files",
            ToolArgs = "{\"query\":\"hello world\"}",
            ToolResult = "found 1 file"
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));
        cut.Find(".tool-header").Click();

        // Click the first copy button (Arguments)
        cut.FindAll(".tool-copy-btn")[0].Click();

        Assert.Contains(_ctx.JSInterop.Invocations, invocation =>
            invocation.Identifier == "BotNexus.copyToClipboard" &&
            invocation.Arguments.Count == 1 &&
            invocation.Arguments[0] is string copiedText &&
            copiedText == "{\"query\":\"hello world\"}");
    }

    [Fact]
    public void Clicking_tool_result_copy_button_invokes_clipboard_with_raw_result()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Tool", "", DateTimeOffset.UtcNow)
        {
            IsToolCall = true,
            ToolName = "search_files",
            ToolArgs = "{\"query\":\"test\"}",
            ToolResult = "found 3 files:\n- a.cs\n- b.cs\n- c.cs"
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));
        cut.Find(".tool-header").Click();

        // Click the second copy button (Result)
        cut.FindAll(".tool-copy-btn")[1].Click();

        Assert.Contains(_ctx.JSInterop.Invocations, invocation =>
            invocation.Identifier == "BotNexus.copyToClipboard" &&
            invocation.Arguments.Count == 1 &&
            invocation.Arguments[0] is string copiedText &&
            copiedText == "found 3 files:\n- a.cs\n- b.cs\n- c.cs");
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

    [Fact]
    public void Renders_ask_user_prompt_and_hides_standard_chat_input_when_pending_prompt_exists()
    {
        CreateAndSeedAgent("agent-1", isConnected: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.SetPendingAskUser(new AskUserPromptState
        {
            RequestId = "req-1",
            ConversationId = "conv-1",
            Prompt = "What should I do next?",
            InputType = "FreeForm",
            AllowFreeForm = true
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        cut.Find(".ask-user-prompt");
        Assert.Empty(cut.FindAll(".chat-input"));
    }

    [Fact]
    public async Task Submitting_ask_user_prompt_calls_interaction_service_and_appends_summary_message()
    {
        CreateAndSeedAgent("agent-1", isConnected: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.SetPendingAskUser(new AskUserPromptState
        {
            RequestId = "req-1",
            ConversationId = "conv-1",
            Prompt = "Share details",
            InputType = "FreeForm",
            AllowFreeForm = true
        });

        _interaction
            .RespondToAskUserAsync("conv-1", "req-1", "My answer", null, false)
            .Returns(Task.CompletedTask);

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));
        cut.Find(".ask-user-free-form").Input("My answer");

        await cut.InvokeAsync(() => cut.Find(".ask-user-actions .send-btn").Click());

        await _interaction.Received(1).RespondToAskUserAsync("conv-1", "req-1", "My answer", null, false);
        Assert.Null(_store.GetPendingAskUser("conv-1"));
        Assert.Contains(_store.GetMessages("conv-1"), message => message.Content.Contains("You answered:", StringComparison.Ordinal));
    }

}

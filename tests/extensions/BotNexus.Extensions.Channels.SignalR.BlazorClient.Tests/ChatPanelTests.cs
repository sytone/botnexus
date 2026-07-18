using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
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
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(new SlashCommandDispatcher(_interaction));
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
        _ctx.JSInterop.SetupVoid("chatAttachments.bindPaste", _ => true);
        _ctx.JSInterop.SetupVoid("chatScroll.observeTopForLoadMore", _ => true);
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
        _ctx.JSInterop.SetupVoid("chatScroll.observeTopForLoadMore", _ => true);

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
        // #1475: user messages now render through the Markdown pipeline (plain text still shows verbatim).
        _ctx.JSInterop.SetupVoid("BotNexus.attachCodeCopyButtons", _ => true);
        _ctx.JSInterop.Setup<string>("BotNexus.renderMarkdown", _ => true).SetResult("<p>Hello from user!</p>");

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

    // #1651 (post-as-assistant Step 3/3): an agent post stamped `assistant` in Step 2
    // (#1650) reaches the client with Role == "Assistant" (via history replay's MapRole
    // or the role-carrying live fan-out). The render must show it as an ASSISTANT bubble,
    // never a user bubble -- the whole point of the epic. Locks the assistant-vs-user
    // decision to msg.Role so a future refactor cannot silently regress it.
    [Fact]
    public void Assistant_stamped_agent_post_renders_as_assistant_bubble_not_user()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        // An agent-authored post the gateway stamped MessageRole.Assistant.
        _store.AppendMessage("conv-1", new ChatMessage("Assistant", "Posting as myself", DateTimeOffset.UtcNow));

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Single(cut.FindAll(".message.assistant"));
        Assert.Empty(cut.FindAll(".message.user"));
        var bubble = cut.Find("[data-message-role]");
        Assert.Equal("Assistant", bubble.GetAttribute("data-message-role"));
    }

    // #1651: the on-behalf-of-user kickoff explicitly stamps MessageRole.User even though
    // the sender is an agent (speak_as:"user" in Step 2). That post must still render as a
    // USER bubble -- the Hybrid rule's explicit override is honoured all the way to the DOM.
    [Fact]
    public void User_stamped_agent_post_renders_as_user_bubble_not_assistant()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        // An agent-authored post the gateway stamped MessageRole.User (on-behalf-of-user kickoff).
        _store.AppendMessage("conv-1", new ChatMessage("User", "Kicking off on behalf of the user", DateTimeOffset.UtcNow));

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Single(cut.FindAll(".message.user"));
        Assert.Empty(cut.FindAll(".message.assistant"));
        var bubble = cut.Find("[data-message-role]");
        Assert.Equal("User", bubble.GetAttribute("data-message-role"));
    }

    // #1651: no regression for genuine human-authored messages -- a User-role message keeps
    // rendering as a user bubble exactly as before the epic (guards against an over-broad
    // change that would reclassify human input while wiring the assistant path).
    [Fact]
    public void Human_authored_user_message_still_renders_as_user_bubble()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("User", "A human typed this", DateTimeOffset.UtcNow));

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Single(cut.FindAll(".message.user"));
        Assert.Empty(cut.FindAll(".message.assistant"));
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
    public void Does_not_render_streaming_bubble_when_run_active_but_no_message_streaming()
    {
        // Streaming-flash regression: after send, RunStarted flips IsRunActive true (so IsTurnActive
        // is true) BEFORE the first MessageStart. If the live bubble keyed off the broad turn-active
        // signal it would paint any residual buffer as RAW text in that pre-token window. The bubble
        // must instead key off the narrow per-message IsStreaming flag, so nothing renders until real
        // content is arriving.
        CreateAndSeedAgent("agent-1", isStreaming: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");

        var conv = _store.GetAgent("agent-1")!.Conversations["conv-1"];
        conv.StreamState.IsRunActive = true;      // RunStarted seen
        conv.StreamState.IsStreaming = false;     // MessageStart NOT yet seen
        conv.StreamState.Buffer = "# residual **markdown**";

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Empty(cut.FindAll("[data-testid='streaming-message']"));
        Assert.DoesNotContain("residual", cut.Markup);
    }

    [Fact]
    public void Renders_streaming_bubble_once_message_streaming_flag_is_set()
    {
        // Complement to the flash guard: once MessageStart asserts the per-message IsStreaming flag,
        // the live bubble renders the accumulating buffer as expected.
        CreateAndSeedAgent("agent-1", isStreaming: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");

        var conv = _store.GetAgent("agent-1")!.Conversations["conv-1"];
        conv.StreamState.IsRunActive = true;
        conv.StreamState.IsStreaming = true;      // MessageStart seen
        conv.StreamState.Buffer = "live tokens";

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        cut.Find("[data-testid='streaming-message']");
        Assert.Contains("live tokens", cut.Markup);
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
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
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
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
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


    [Fact]
    public void InterruptSteerButton_HasCorrectCssClass_WhenStreaming()
    {
        CreateAndSeedAgent("agent-1", isStreaming: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.SetStreaming("conv-1", true);

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        var btn = cut.Find(".interrupt-steer-btn");
        Assert.NotNull(btn);
    }

    [Fact]
    public void InterruptSteerButton_HasAbbreviatedLabel_WhenStreaming()
    {
        CreateAndSeedAgent("agent-1", isStreaming: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.SetStreaming("conv-1", true);

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        var btn = cut.Find(".interrupt-steer-btn");
        Assert.Contains("Redirect", btn.TextContent);
        Assert.DoesNotContain("Interrupt + Redirect", btn.TextContent);
    }

    [Fact]
    public void InterruptSteerButton_IsNotRendered_WhenNotStreaming()
    {
        CreateAndSeedAgent("agent-1", isStreaming: false);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Empty(cut.FindAll(".interrupt-steer-btn"));
    }

    [Fact]
    public void ThinkingOnly_Message_Does_Not_Render_Empty_Message_Bubble()
    {
        CreateAndSeedAgent("agent-1", isConnected: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Assistant", "", DateTimeOffset.UtcNow)
        {
            ThinkingContent = "I am reasoning about this..."
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        // Thinking block should render
        Assert.Single(cut.FindAll(".thinking-block"));
        // No assistant message bubble should render (content is empty)
        Assert.Empty(cut.FindAll(".message.assistant"));
    }

    [Fact]
    public void ThinkingWithContent_Message_Renders_Both_ThinkingBlock_And_MessageBubble()
    {
        CreateAndSeedAgent("agent-1", isConnected: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Assistant", "Here is my answer.", DateTimeOffset.UtcNow)
        {
            ThinkingContent = "Let me think about this..."
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        // Both should render
        Assert.Single(cut.FindAll(".thinking-block"));
        Assert.Single(cut.FindAll(".message.assistant"));
    }

    [Fact]
    public void ThinkingBlock_Details_Element_Has_Open_Attribute_By_Default()
    {
        CreateAndSeedAgent("agent-1", isConnected: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Assistant", "Answer", DateTimeOffset.UtcNow)
        {
            ThinkingContent = "Some reasoning..."
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        var details = cut.Find(".thinking-block details");
        Assert.True(details.HasAttribute("open"), "Thinking <details> should have the 'open' attribute by default.");
    }

    [Fact]
    public void Normal_Assistant_Message_Without_Thinking_Renders_MessageBubble()
    {
        CreateAndSeedAgent("agent-1", isConnected: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Assistant", "Hello world", DateTimeOffset.UtcNow));

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Empty(cut.FindAll(".thinking-block"));
        Assert.Single(cut.FindAll(".message.assistant"));
    }

    // #1475: user messages must render through the same Markdown pipeline as assistant messages.
    [Fact]
    public void Renders_user_message_as_markdown_markup()
    {
        CreateAndSeedAgent("agent-1", isConnected: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("User", "**bold**", DateTimeOffset.UtcNow));
        _ctx.JSInterop.SetupVoid("BotNexus.attachCodeCopyButtons", _ => true);
        _ctx.JSInterop.Setup<string>("BotNexus.renderMarkdown", _ => true).SetResult("<p><strong>bold</strong></p>");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        // The user message bubble must render the sanitized HTML (msg-content), not the raw markdown source.
        var userBubble = cut.Find(".message.user");
        Assert.Contains("<strong>bold</strong>", userBubble.InnerHtml);
        Assert.DoesNotContain("**bold**", userBubble.InnerHtml);
    }

    [Fact]
    public void Renders_user_message_through_render_markdown_js()
    {
        CreateAndSeedAgent("agent-1", isConnected: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("User", "- a\n- b", DateTimeOffset.UtcNow));
        _ctx.JSInterop.SetupVoid("BotNexus.attachCodeCopyButtons", _ => true);
        _ctx.JSInterop.Setup<string>("BotNexus.renderMarkdown", _ => true).SetResult("<ul><li>a</li><li>b</li></ul>");

        _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Contains(_ctx.JSInterop.Invocations, i =>
            i.Identifier == "BotNexus.renderMarkdown"
            && i.Arguments.Count == 1
            && i.Arguments[0] is string s && s == "- a\n- b");
    }

    [Fact]
    public void Attaches_code_copy_buttons_after_rendering_user_markdown()
    {
        // Acceptance criteria: code blocks in user messages get the existing code-copy button treatment
        // (BotNexus.attachCodeCopyButtons runs over the rendered container) without adding the
        // message-level header copy button (.msg-copy-btn stays assistant-only).
        CreateAndSeedAgent("agent-1", isConnected: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("User", "```csharp\nx();\n```", DateTimeOffset.UtcNow));
        _ctx.JSInterop.SetupVoid("BotNexus.attachCodeCopyButtons", _ => true);
        _ctx.JSInterop.Setup<string>("BotNexus.renderMarkdown", _ => true).SetResult("<pre><code>x();</code></pre>");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        // User code renders through markdown (msg-content), and the code-copy hook runs.
        var userBubble = cut.Find(".message.user");
        Assert.Contains("<pre><code>x();</code></pre>", userBubble.InnerHtml);
        Assert.Contains(_ctx.JSInterop.Invocations, i => i.Identifier == "BotNexus.attachCodeCopyButtons");
        // The whole-message header copy button stays assistant-only (#1475 does not change it).
        Assert.Empty(userBubble.QuerySelectorAll(".msg-copy-btn"));
    }

    [Fact]
    public void Does_not_render_system_message_as_markdown_markup()
    {
        // System/Tool/Error rendering must be unchanged (not routed through the markdown cache).
        CreateAndSeedAgent("agent-1", isConnected: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("System", "**not rendered**", DateTimeOffset.UtcNow));
        _ctx.JSInterop.SetupVoid("BotNexus.attachCodeCopyButtons", _ => true);
        _ctx.JSInterop.Setup<string>("BotNexus.renderMarkdown", _ => true).SetResult("<p><strong>not rendered</strong></p>");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Contains("**not rendered**", cut.Markup);
    }

    // ── #1691: scroll-up history pagination (load-more) ───────────────────

    private ConversationState SeedActiveConversationWithMore(string agentId, string convId)
    {
        _store.SeedConversations(agentId, [MakeConvDto(convId, agentId)]);
        _store.SetActiveConversation(agentId, convId);
        var conv = _store.GetConversation(convId)!;
        conv.HistoryLoaded = true;
        conv.HasMoreHistory = true;
        conv.LoadedHistoryRows = 20;
        return conv;
    }

    [Fact]
    public void Load_more_affordance_is_shown_when_more_history_exists()
    {
        // #1691: a conversation that opened on the most-recent page but has older messages must
        // surface the scroll-up load-more affordance.
        CreateAndSeedAgent("agent-1", isConnected: true);
        SeedActiveConversationWithMore("agent-1", "conv-1");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.NotNull(cut.Find("[data-testid='chat-load-more']"));
    }

    [Fact]
    public void Load_more_affordance_is_hidden_when_no_more_history()
    {
        CreateAndSeedAgent("agent-1", isConnected: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        var conv = _store.GetConversation("conv-1")!;
        conv.HistoryLoaded = true;
        conv.HasMoreHistory = false;

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Empty(cut.FindAll("[data-testid='chat-load-more']"));
    }

    [Fact]
    public async Task Scroll_to_top_fetches_next_page_via_shared_service()
    {
        // #1691: the JSInvokable the scroll observer fires must delegate to the shared Core paging
        // method so desktop and mobile use one implementation. Simulate the older page being
        // prepended so the component does not early-return on a stale HasMoreHistory check.
        CreateAndSeedAgent("agent-1", isConnected: true);
        var conv = SeedActiveConversationWithMore("agent-1", "conv-1");
        _interaction.LoadMoreHistoryAsync("agent-1", "conv-1").Returns(callInfo =>
        {
            _store.PrependMessages("conv-1", Enumerable.Range(0, 20)
                .Select(i => new ChatMessage("User", $"older-{i}", DateTimeOffset.UtcNow)));
            conv.LoadedHistoryRows += 20;
            return 20;
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        await cut.InvokeAsync(() => cut.Instance.OnScrolledToTop());

        await _interaction.Received(1).LoadMoreHistoryAsync("agent-1", "conv-1");
        Assert.Equal(20, conv.Messages.Count);
        Assert.Equal("older-0", conv.Messages[0].Content);
    }

    [Fact]
    public async Task Load_more_button_click_fetches_next_page()
    {
        CreateAndSeedAgent("agent-1", isConnected: true);
        SeedActiveConversationWithMore("agent-1", "conv-1");
        _interaction.LoadMoreHistoryAsync("agent-1", "conv-1").Returns(0);

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        await cut.InvokeAsync(() => cut.Find("[data-testid='chat-load-more-btn']").Click());

        await _interaction.Received(1).LoadMoreHistoryAsync("agent-1", "conv-1");
    }

    [Fact]
    public async Task Scroll_to_top_is_noop_when_no_more_history()
    {
        // Guard: once exhausted, the scroll observer firing again must not call the service.
        CreateAndSeedAgent("agent-1", isConnected: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        var conv = _store.GetConversation("conv-1")!;
        conv.HistoryLoaded = true;
        conv.HasMoreHistory = false;

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        await cut.InvokeAsync(() => cut.Instance.OnScrolledToTop());

        await _interaction.DidNotReceive().LoadMoreHistoryAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    // ── #1894: unkeyed @foreach garble regression ───────────────────────────
    // The desktop portal garbled live-streaming messages (each fragment appearing
    // twice: once truncated at a delta boundary, once as a longer restatement) while
    // rendering fine on reload-from-store. Root cause: the message @foreach had no
    // @key, so Blazor diffed children positionally. When a committed message was
    // appended mid-stream (list length grows in the same render batch that removes
    // the streaming bubble), positional diffing retargeted retained text nodes onto
    // the wrong logical message, duplicating streamed fragments in the DOM. The fix
    // wraps each loop item in a @key="msg.Id" element and gives the streaming bubble
    // a stable sentinel key so it is never diff-matched against a committed message.

    [Fact]
    public void Each_message_row_is_keyed_by_message_id()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("User", "first", DateTimeOffset.UtcNow));
        _store.AppendMessage("conv-1", new ChatMessage("Assistant", "second", DateTimeOffset.UtcNow));

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        // Each loop item is emitted inside a display:contents keyed wrapper. Two
        // messages -> at least two such wrappers (keying is structural, so the
        // presence of the wrapper is what the renderer needs to diff by identity).
        var wrappers = cut.FindAll("div[style*='display:contents']");
        Assert.True(wrappers.Count >= 2,
            $"expected a keyed display:contents wrapper per message, found {wrappers.Count}");
    }

    [Fact]
    public void Streaming_then_commit_does_not_duplicate_content()
    {
        CreateAndSeedAgent("agent-1", isStreaming: true);
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.SetStreaming("conv-1", true);
        // The committed assistant message renders through the Markdown pipeline
        // (#1475), so the JS renderer must be mocked or the message body renders empty.
        _ctx.JSInterop.SetupVoid("BotNexus.attachCodeCopyButtons", _ => true);
        _ctx.JSInterop.Setup<string>("BotNexus.renderMarkdown", _ => true).SetResult("<p>Hello world</p>");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        // Stream two deltas, then commit the buffer as a final assistant message and
        // clear the streaming flag -- exactly the length-changing terminal-flush
        // transition that used to corrupt positional diffing.
        cut.InvokeAsync(() =>
        {
            _store.AppendStreamBuffer("conv-1", "Hello ");
            _store.AppendStreamBuffer("conv-1", "world");
        });
        cut.Render();

        cut.InvokeAsync(() =>
        {
            _store.CommitStreamBuffer("conv-1");
            _store.SetStreaming("conv-1", false);
        });
        cut.Render();

        // The committed message must appear exactly once, and the transient streaming
        // bubble must be gone.
        Assert.Empty(cut.FindAll("[data-testid='streaming-message']"));
        var committed = _store.GetMessages("conv-1");
        Assert.Single(committed);
        Assert.Equal("Hello world", committed[0].Content);

        // No duplicated fragment: "Hello world" occurs once, not twice, in the DOM.
        var occurrences = CountOccurrences(cut.Markup, "Hello world");
        Assert.Equal(1, occurrences);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    // ─────────────────────────────────────────────────────────────────────
    // #1948 — Tool pop-out modal: decode function unit tests
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void DecodeToolPayload_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ChatPanel.DecodeToolPayload(null));
        Assert.Equal(string.Empty, ChatPanel.DecodeToolPayload(""));
    }

    [Fact]
    public void DecodeToolPayload_PlainText_UnescapesNewlineAndTab()
    {
        var raw = "line1\\nline2\\tcol";
        var decoded = ChatPanel.DecodeToolPayload(raw);
        Assert.Equal("line1\nline2\tcol", decoded);
    }

    [Fact]
    public void DecodeToolPayload_PlainText_UnescapesUnicodeEscapeToGlyph()
    {
        // \u2705 == ✅ (WHITE HEAVY CHECK MARK)
        var raw = "status \\u2705 done";
        var decoded = ChatPanel.DecodeToolPayload(raw);
        Assert.Equal("status \u2705 done", decoded);
        Assert.Contains("\u2705", decoded);
        Assert.DoesNotContain("\\u2705", decoded);
    }

    [Fact]
    public void DecodeToolPayload_ValidJson_IsPrettyPrinted()
    {
        var raw = "{\"query\":\"hello\",\"count\":3}";
        var decoded = ChatPanel.DecodeToolPayload(raw);

        // Pretty-printed JSON spans multiple lines and is indented.
        Assert.Contains("\n", decoded);
        Assert.Contains("\"query\"", decoded);
        Assert.Contains("  ", decoded);
    }

    [Fact]
    public void DecodeToolPayload_JsonArray_IsPrettyPrinted()
    {
        var raw = "[{\"name\":\"alpha\"},{\"name\":\"beta\"}]";

        var decoded = ChatPanel.DecodeToolPayload(raw);

        Assert.Equal("[\n  {\n    \"name\": \"alpha\"\n  },\n  {\n    \"name\": \"beta\"\n  }\n]", decoded.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void DecodeToolPayload_JsonEncodedInsideString_IsPrettyPrinted()
    {
        var raw = "\"{\\\"name\\\":\\\"alpha\\\",\\\"count\\\":2}\"";

        var decoded = ChatPanel.DecodeToolPayload(raw);

        Assert.Equal("{\n  \"name\": \"alpha\",\n  \"count\": 2\n}", decoded.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void DecodeToolPayload_JsonWithEscapedNewlinesInStringValue_RendersRealNewlines()
    {
        // The JSON string value contains an escaped newline + a unicode escape.
        var raw = "{\"result\":\"a\\nb \\u2705\"}";
        var decoded = ChatPanel.DecodeToolPayload(raw);

        // Pretty-printer decodes the string content: real newline + real glyph.
        Assert.Contains("a\nb", decoded);
        Assert.Contains("\u2705", decoded);
        Assert.DoesNotContain("\\u2705", decoded);
    }

    [Fact]
    public void DecodeToolPayload_InvalidJson_FallsBackToUnescapedVerbatim()
    {
        // Looks like JSON (starts with '{') but is malformed — must not throw,
        // should fall back to backslash-unescaping.
        var raw = "{not valid json\\nsecond line";
        var decoded = ChatPanel.DecodeToolPayload(raw);
        Assert.Equal("{not valid json\nsecond line", decoded);
    }

    [Fact]
    public void DecodeToolPayload_UnknownEscape_KeepsBackslashVerbatim()
    {
        var raw = "path C:\\xtemp";
        var decoded = ChatPanel.DecodeToolPayload(raw);
        // \x is not a known escape — the backslash is preserved.
        Assert.Equal("path C:\\xtemp", decoded);
    }

    [Fact]
    public void DecodeToolPayload_DoesNotDecodeHtml_NoMarkupInjection()
    {
        // Ensure the decoder does not turn HTML entities into markup — it only
        // touches backslash escapes. XSS safety comes from <pre> HTML-encoding,
        // but the decoded text itself must remain literal.
        var raw = "<script>alert(1)</script>\\nnext";
        var decoded = ChatPanel.DecodeToolPayload(raw);
        Assert.Equal("<script>alert(1)</script>\nnext", decoded);
    }

    // ─────────────────────────────────────────────────────────────────────
    // #1948 — Tool pop-out modal: render/interaction tests
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_popout_buttons_in_expanded_tool_sections()
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
        Assert.Empty(cut.FindAll(".tool-popout-btn"));

        cut.Find(".tool-header").Click();

        // Two pop-out buttons: Arguments + Result
        Assert.Equal(2, cut.FindAll(".tool-popout-btn").Count);
    }

    [Fact]
    public void Clicking_popout_opens_modal_with_decoded_content()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Tool", "", DateTimeOffset.UtcNow)
        {
            IsToolCall = true,
            ToolName = "search_files",
            ToolArgs = "{\"query\":\"test\"}",
            ToolResult = "line1\\nline2 \\u2705"
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));
        cut.Find(".tool-header").Click();

        // No modal yet
        Assert.Empty(cut.FindAll(".tool-modal-overlay"));

        // Click the Result pop-out button (second one)
        cut.FindAll(".tool-popout-btn")[1].Click();

        var modal = cut.Find(".tool-modal-content");
        // Decoded: escaped \n and \uXXXX become real newline + glyph.
        Assert.Contains("line1\nline2", modal.TextContent);
        Assert.Contains("\u2705", modal.TextContent);
        Assert.DoesNotContain("\\u2705", modal.TextContent);
    }

    [Fact]
    public void Tool_modal_closes_via_close_button()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Tool", "", DateTimeOffset.UtcNow)
        {
            IsToolCall = true,
            ToolName = "search_files",
            ToolArgs = "{\"query\":\"test\"}"
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));
        cut.Find(".tool-header").Click();
        cut.FindAll(".tool-popout-btn")[0].Click();

        Assert.Single(cut.FindAll(".tool-modal-overlay"));

        cut.Find(".tool-modal-close").Click();
        Assert.Empty(cut.FindAll(".tool-modal-overlay"));
    }

    [Fact]
    public void Tool_modal_closes_via_escape_key()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Tool", "", DateTimeOffset.UtcNow)
        {
            IsToolCall = true,
            ToolName = "search_files",
            ToolArgs = "{\"query\":\"test\"}"
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));
        cut.Find(".tool-header").Click();
        cut.FindAll(".tool-popout-btn")[0].Click();

        Assert.Single(cut.FindAll(".tool-modal-overlay"));

        cut.Find(".tool-modal-overlay").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(cut.FindAll(".tool-modal-overlay"));
    }

    [Fact]
    public void Tool_modal_closes_via_backdrop_click()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Tool", "", DateTimeOffset.UtcNow)
        {
            IsToolCall = true,
            ToolName = "search_files",
            ToolArgs = "{\"query\":\"test\"}"
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));
        cut.Find(".tool-header").Click();
        cut.FindAll(".tool-popout-btn")[0].Click();

        Assert.Single(cut.FindAll(".tool-modal-overlay"));

        // Clicking the overlay backdrop (not the dialog) closes.
        cut.Find(".tool-modal-overlay").Click();
        Assert.Empty(cut.FindAll(".tool-modal-overlay"));
    }

    [Fact]
    public void Copy_button_still_copies_raw_payload_even_with_escapes()
    {
        CreateAndSeedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.AppendMessage("conv-1", new ChatMessage("Tool", "", DateTimeOffset.UtcNow)
        {
            IsToolCall = true,
            ToolName = "search_files",
            ToolResult = "line1\\nline2 \\u2705"
        });

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));
        cut.Find(".tool-header").Click();

        // Copy button copies the ORIGINAL raw payload (lossless — escapes intact).
        cut.FindAll(".tool-copy-btn")[0].Click();

        Assert.Contains(_ctx.JSInterop.Invocations, invocation =>
            invocation.Identifier == "BotNexus.copyToClipboard" &&
            invocation.Arguments.Count == 1 &&
            invocation.Arguments[0] is string copiedText &&
            copiedText == "line1\\nline2 \\u2705");
    }

    [Fact]
    public void Command_palette_renders_full_shared_registry_surface()
    {
        CreateAndSeedAgent("agent-1", isConnected: true);
        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        var input = cut.Find(".chat-input");
        input.Input("/");

        var items = cut.FindAll(".command-palette .command-item");
        Assert.Equal(SlashCommandRegistry.All.Count, items.Count);
        Assert.Contains("/prompts", cut.Markup);
        Assert.Contains("/reasoning", cut.Markup);
        Assert.Contains("/help", cut.Markup);
    }

    [Fact]
    public async Task Executing_new_command_from_palette_resets_session_via_dispatcher()
    {
        CreateAndSeedAgent("agent-1", isConnected: true);
        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        var input = cut.Find(".chat-input");
        input.Input("/new");

        await cut.Find(".command-palette .command-item").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        await _interaction.Received(1).ResetSessionAsync("agent-1");
    }
}
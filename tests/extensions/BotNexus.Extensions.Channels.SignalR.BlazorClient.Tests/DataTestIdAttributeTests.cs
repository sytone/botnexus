using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Verifies that all required data-testid attributes (Issue #636) are present
/// on their corresponding UI elements across the portal components.
/// </summary>
public sealed class DataTestIdAttributeTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store;

    public DataTestIdAttributeTests()
    {
        _store = new ClientStateStore();
        var interaction = Substitute.For<IAgentInteractionService>();
        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(false);
        portalLoad.IsLoading.Returns(true);
        portalLoad.LoadError.Returns((string?)null);

        var hub = new GatewayHubConnection();
        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.ApiBaseUrl.Returns("");
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        var gatewayInfo = new GatewayInfoService(http, restClient);
        var mockPrefs = Substitute.For<IPortalPreferencesService>();
        mockPrefs.Current.Returns(new PortalPreferences());

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(interaction);
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(portalLoad);
        _ctx.Services.AddSingleton(hub);
        _ctx.Services.AddSingleton(gatewayInfo);
        _ctx.Services.AddSingleton(Substitute.For<IUpdateStatusService>());
        _ctx.Services.AddSingleton(mockPrefs);
        _ctx.Services.AddSingleton(restClient);
        _ctx.Services.AddSingleton(Substitute.For<IChannelErrorReporter>());
        _ctx.Services.AddSingleton(http);
        _ctx.Services.AddSingleton(new ExtensionFeatureService(restClient));
        _ctx.Services.AddSingleton(new CronApiClient(http));
        _ctx.Services.AddSingleton(new SectionsApiClient(http));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    // ───────────────────────────────────────────────────────────────────────
    // MainLayout tests
    // ───────────────────────────────────────────────────────────────────────

    private IRenderedComponent<MainLayout> RenderLayout() =>
        _ctx.Render<MainLayout>(p => p
            .Add(c => c.Body, (RenderFragment)(_ => { })));

    [Fact]
    public void MainLayout_has_banner_settings_btn_testid()
    {
        var cut = RenderLayout();
        cut.Find("[data-testid='banner-settings-btn']");
    }

    [Fact]
    public void MainLayout_has_sidebar_toggle_btn_testid()
    {
        var cut = RenderLayout();
        cut.Find("[data-testid='sidebar-toggle-btn']");
    }

    [Fact]
    public void MainLayout_has_agent_select_testid()
    {
        // Agent dropdown only renders when there are non-read-only agents
        // and device is not mobile. Seed a non-subagent agent to make it visible.
        _store.UpsertAgent(new AgentState
        {
            AgentId = "test-agent",
            DisplayName = "Test Agent"
        });
        _store.SelectView("test-agent", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();
        cut.Find("[data-testid='agent-select']");
    }

    [Fact]
    public void MainLayout_has_new_conversation_btn_testid()
    {
        // Need an active non-read-only agent to show the new button
        var agent = new AgentState
        {
            AgentId = "test-agent",
            DisplayName = "Test Agent"
        };
        _store.UpsertAgent(agent);
        _store.SelectView("test-agent", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();
        cut.Find("[data-testid='new-conversation-btn']");
    }

    [Fact]
    public void MainLayout_has_conversation_archive_btn_testid()
    {
        // Need an active non-read-only agent with a non-default conversation
        var agent = new AgentState
        {
            AgentId = "test-agent",
            DisplayName = "Test Agent"
        };
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Test Conversation",
            IsDefault = false,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        agent.ActiveConversationId = "conv-1";
        _store.UpsertAgent(agent);
        _store.SelectView("test-agent", string.Empty, SelectionSource.UserClick);

        var cut = RenderLayout();
        cut.Find("[data-testid='conversation-archive-btn']");
    }

    // ───────────────────────────────────────────────────────────────────────
    // ChatPanel tests
    // ───────────────────────────────────────────────────────────────────────

    private IRenderedComponent<ChatPanel> RenderChatPanel(string agentId = "test-agent", bool isConnected = true, bool isStreaming = false)
    {
        var agent = new AgentState
        {
            AgentId = agentId,
            DisplayName = "Test Agent",
            IsConnected = isConnected,
            IsStreaming = isStreaming
        };
        _store.UpsertAgent(agent);
        _store.SelectView(agentId, string.Empty, SelectionSource.UserClick);

        return _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, agentId));
    }

    [Fact]
    public void ChatPanel_has_chat_config_btn_testid()
    {
        var cut = RenderChatPanel();
        cut.Find("[data-testid='chat-config-btn']");
    }

    [Fact]
    public void ChatPanel_has_chat_thinking_toggle_testid()
    {
        var cut = RenderChatPanel();
        cut.Find("[data-testid='chat-thinking-toggle']");
    }

    [Fact]
    public void ChatPanel_has_chat_tools_toggle_testid()
    {
        var cut = RenderChatPanel();
        cut.Find("[data-testid='chat-tools-toggle']");
    }

    [Fact]
    public void ChatPanel_has_chat_new_session_btn_testid()
    {
        var cut = RenderChatPanel();
        cut.Find("[data-testid='chat-new-session-btn']");
    }

    [Fact]
    public void ChatPanel_has_chat_abort_btn_testid_when_streaming()
    {
        // Abort button only renders when streaming. Set up a streaming state.
        var agent = new AgentState
        {
            AgentId = "stream-agent",
            DisplayName = "Stream Agent",
            IsConnected = true
        };
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Active",
            IsDefault = true
        };
        agent.ActiveConversationId = "conv-1";
        _store.UpsertAgent(agent);
        _store.SelectView("stream-agent", string.Empty, SelectionSource.UserClick);

        // Mark the conversation as streaming via IsStreaming (IsTurnActive is computed)
        var streamState = _store.GetStreamState("conv-1");
        streamState.IsStreaming = true;

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "stream-agent"));
        cut.Find("[data-testid='chat-abort-btn']");
    }

    [Fact]
    public void ChatPanel_has_chat_steer_btn_testid_when_streaming()
    {
        var agent = new AgentState
        {
            AgentId = "stream-agent",
            DisplayName = "Stream Agent",
            IsConnected = true
        };
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Active",
            IsDefault = true
        };
        agent.ActiveConversationId = "conv-1";
        _store.UpsertAgent(agent);
        _store.SelectView("stream-agent", string.Empty, SelectionSource.UserClick);

        var streamState = _store.GetStreamState("conv-1");
        streamState.IsStreaming = true;

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "stream-agent"));
        cut.Find("[data-testid='chat-steer-btn']");
    }

    [Fact]
    public void ChatPanel_has_chat_followup_btn_testid_when_streaming()
    {
        var agent = new AgentState
        {
            AgentId = "stream-agent",
            DisplayName = "Stream Agent",
            IsConnected = true
        };
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Active",
            IsDefault = true
        };
        agent.ActiveConversationId = "conv-1";
        _store.UpsertAgent(agent);
        _store.SelectView("stream-agent", string.Empty, SelectionSource.UserClick);

        var streamState = _store.GetStreamState("conv-1");
        streamState.IsStreaming = true;

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "stream-agent"));
        cut.Find("[data-testid='chat-followup-btn']");
    }

    [Fact]
    public void ChatPanel_shows_run_controls_in_the_between_tools_gap_when_run_active()
    {
        // End-to-end UI proof of the flicker fix: the run is active (RunStarted seen) but no text
        // is streaming and no tool is in flight -- the gap between two sequential tools. The chat
        // composer must still show the run controls (steer/redirect/follow-up/stop), NOT Send.
        var agent = new AgentState
        {
            AgentId = "stream-agent",
            DisplayName = "Stream Agent",
            IsConnected = true
        };
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Active",
            IsDefault = true
        };
        agent.ActiveConversationId = "conv-1";
        _store.UpsertAgent(agent);
        _store.SelectView("stream-agent", string.Empty, SelectionSource.UserClick);

        var streamState = _store.GetStreamState("conv-1");
        streamState.IsRunActive = true;   // RunStarted seen, RunEnded not yet
        streamState.IsStreaming = false;  // between an LLM generation and the next
        // ActiveToolCalls empty -- between two sequential tools

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "stream-agent"));

        // Run controls present, Send absent.
        cut.Find("[data-testid='chat-steer-btn']");
        cut.Find("[data-testid='chat-followup-btn']");
        cut.Find("[data-testid='chat-abort-btn']");
        Assert.Empty(cut.FindAll("[data-testid='chat-send']"));
    }

    [Fact]
    public void ChatPanel_has_streaming_badge_testid_when_streaming()
    {
        var agent = new AgentState
        {
            AgentId = "stream-agent",
            DisplayName = "Stream Agent",
            IsConnected = true
        };
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Active",
            IsDefault = true
        };
        agent.ActiveConversationId = "conv-1";
        _store.UpsertAgent(agent);
        _store.SelectView("stream-agent", string.Empty, SelectionSource.UserClick);

        var streamState = _store.GetStreamState("conv-1");
        streamState.IsStreaming = true;

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "stream-agent"));
        cut.Find("[data-testid='streaming-badge']");
    }

    [Fact]
    public void ChatPanel_markup_contains_command_palette_testid()
    {
        // The command palette only renders when _showCommandPalette is true (internal state).
        // We verify the testid is in the source markup by checking the Razor output contains it.
        var cut = RenderChatPanel();
        // Command palette is conditionally rendered; verify the attribute string is present
        // by triggering slash input. Since JS interop is loose, we can't fully drive the
        // input, so we assert the markup template at least compiles with the attribute.
        Assert.Contains("data-testid=\"command-palette\"", GetComponentFileContent("ChatPanel.razor"));
    }

    // ───────────────────────────────────────────────────────────────────────
    // AskUserPrompt tests
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void AskUserPrompt_has_ask_user_prompt_testid()
    {
        var cut = _ctx.Render<AskUserPrompt>(p => p
            .Add(c => c.Prompt, new AskUserPromptState
            {
                RequestId = "r1",
                ConversationId = "c1",
                Prompt = "Question?",
                InputType = "FreeForm"
            }));

        cut.Find("[data-testid='ask-user-prompt']");
    }

    [Fact]
    public void AskUserPrompt_has_ask_user_submit_testid()
    {
        var cut = _ctx.Render<AskUserPrompt>(p => p
            .Add(c => c.Prompt, new AskUserPromptState
            {
                RequestId = "r1",
                ConversationId = "c1",
                Prompt = "Question?",
                InputType = "FreeForm"
            }));

        cut.Find("[data-testid='ask-user-submit']");
    }

    [Fact]
    public void AskUserPrompt_has_ask_user_cancel_testid()
    {
        var cut = _ctx.Render<AskUserPrompt>(p => p
            .Add(c => c.Prompt, new AskUserPromptState
            {
                RequestId = "r1",
                ConversationId = "c1",
                Prompt = "Question?",
                InputType = "FreeForm"
            }));

        cut.Find("[data-testid='ask-user-cancel']");
    }

    // ───────────────────────────────────────────────────────────────────────
    // AgentPanel tests
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void AgentPanel_has_agent_tab_testids()
    {
        _store.UpsertAgent(new AgentState
        {
            AgentId = "tab-agent",
            DisplayName = "Tab Agent",
            IsConnected = true
        });
        _store.SelectView("tab-agent", string.Empty, SelectionSource.UserClick);

        var cut = _ctx.Render<AgentPanel>(p => p.Add(c => c.AgentId, "tab-agent"));

        // Should have tab buttons with data-testid pattern
        cut.Find("[data-testid='agent-tab-conversation']");
        cut.Find("[data-testid='agent-tab-workspace']");
        cut.Find("[data-testid='agent-tab-reports']");
        cut.Find("[data-testid='agent-tab-canvas']");
    }

    // ───────────────────────────────────────────────────────────────────────
    // PortalSettingsPanel tests
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void PortalSettingsPanel_has_portal_settings_panel_testid()
    {
        // PortalSettingsPanel is conditionally visible; verify markup source
        Assert.Contains("data-testid=\"portal-settings-panel\"",
            GetComponentFileContent("PortalSettingsPanel.razor"));
    }

    [Fact]
    public void PortalSettingsPanel_has_portal_settings_close_testid()
    {
        Assert.Contains("data-testid=\"portal-settings-close\"",
            GetComponentFileContent("PortalSettingsPanel.razor"));
    }

    // ───────────────────────────────────────────────────────────────────────
    // ConnectionStatus tests
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConnectionStatus_has_connection_status_testid()
    {
        var hub = new GatewayHubConnection();
        var cut = _ctx.Render<ConnectionStatus>(p => p.Add(c => c.Hub, hub));

        cut.Find("[data-testid='connection-status']");
    }

    // ───────────────────────────────────────────────────────────────────────
    // Helpers
    // ───────────────────────────────────────────────────────────────────────

    private static string GetComponentFileContent(string fileName)
    {
        // Walk up from the test output directory to find the source file
        var basePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "src", "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient");

        var componentsPath = Path.Combine(basePath, "Components", fileName);
        if (File.Exists(componentsPath))
            return File.ReadAllText(componentsPath);

        var layoutPath = Path.Combine(basePath, "Layout", fileName);
        if (File.Exists(layoutPath))
            return File.ReadAllText(layoutPath);

        throw new FileNotFoundException($"Could not find component file: {fileName}");
    }
}

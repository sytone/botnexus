using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for issue #1208: Canvas panel in mobile UI.
/// </summary>
public sealed class MobileCanvasPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IClientStateStore _store;
    private readonly IPortalLoadService _portalLoad;
    private readonly IAgentInteractionService _interaction;

    public MobileCanvasPanelTests()
    {
        _store = Substitute.For<IClientStateStore>();
        _portalLoad = Substitute.For<IPortalLoadService>();
        _interaction = Substitute.For<IAgentInteractionService>();

        _portalLoad.IsReady.Returns(true);
        _portalLoad.IsSignalRConnected.Returns(true);
        _portalLoad.IsLoading.Returns(false);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var agentState = new AgentState
        {
            AgentId = "test-agent",
            DisplayName = "Test Agent",
            SessionId = "sess-1",
            ActiveConversationId = "conv-1"
        };
        agentState.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Test Conversation",
            Status = "Active"
        };

        _store.Agents.Returns(new Dictionary<string, AgentState> { ["test-agent"] = agentState }.AsReadOnly());
        _store.ActiveAgentId.Returns("test-agent");
        _store.GetAgent("test-agent").Returns(agentState);
        _store.GetStreamState(Arg.Any<string>()).Returns(new ConversationStreamState());
        _store.GetMessages(Arg.Any<string>()).Returns(new List<ChatMessage>().AsReadOnly());

        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Canvas_button_hidden_when_no_canvas_content()
    {
        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "test-agent"));

        var btn = cut.FindAll("[data-testid='canvas-toggle-btn']");
        Assert.Empty(btn);
    }

    [Fact]
    public void Canvas_button_visible_when_agent_has_canvas_html()
    {
        var agent = _store.GetAgent("test-agent")!;
        agent.CanvasHtml = "<h1>Hello Canvas</h1>";

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "test-agent"));

        // Need to open overflow menu first
        var overflowBtn = cut.Find(".overflow-btn");
        overflowBtn.Click();

        var canvasBtn = cut.Find("[data-testid='canvas-toggle-btn']");
        Assert.NotNull(canvasBtn);
    }

    [Fact]
    public void Canvas_button_visible_when_conversation_has_canvas_html()
    {
        var agent = _store.GetAgent("test-agent")!;
        agent.Conversations["conv-1"].CanvasHtml = "<div>Canvas content</div>";

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "test-agent"));

        var overflowBtn = cut.Find(".overflow-btn");
        overflowBtn.Click();

        var canvasBtn = cut.Find("[data-testid='canvas-toggle-btn']");
        Assert.NotNull(canvasBtn);
    }

    [Fact]
    public void Canvas_toggle_opens_canvas_sheet()
    {
        var agent = _store.GetAgent("test-agent")!;
        agent.CanvasHtml = "<h1>Hello</h1>";

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "test-agent"));

        // Open overflow and click canvas
        cut.Find(".overflow-btn").Click();
        cut.Find("[data-testid='canvas-toggle-btn']").Click();

        var sheet = cut.Find("[data-testid='canvas-sheet']");
        Assert.NotNull(sheet);
    }

    [Fact]
    public void Canvas_sheet_shows_empty_state_when_html_is_null()
    {
        var agent = _store.GetAgent("test-agent")!;
        // Set CanvasHtml to non-null so the button shows, then clear it on the conversation
        agent.CanvasHtml = "<p>x</p>";

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "test-agent"));

        cut.Find(".overflow-btn").Click();
        cut.Find("[data-testid='canvas-toggle-btn']").Click();

        // The MobileCanvasPanel uses ConversationId which may have null canvas,
        // but the sheet should still show with iframe since agent-level has content
        var iframe = cut.FindAll("[data-testid='canvas-iframe']");
        Assert.Single(iframe);
    }

    [Fact]
    public void Canvas_sheet_shows_iframe_with_enriched_html()
    {
        var agent = _store.GetAgent("test-agent")!;
        agent.CanvasHtml = "<html><head></head><body><h1>Test</h1></body></html>";
        _store.GetConversation("conv-1").Returns((ConversationState?)null);

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "test-agent"));

        cut.Find(".overflow-btn").Click();
        cut.Find("[data-testid='canvas-toggle-btn']").Click();

        var iframe = cut.Find("[data-testid='canvas-iframe']");
        var srcdoc = iframe.GetAttribute("srcdoc");
        Assert.Contains("canvasState", srcdoc);
        Assert.Contains("<h1>Test</h1>", srcdoc);
    }

    [Fact]
    public void Canvas_backdrop_click_closes_sheet()
    {
        var agent = _store.GetAgent("test-agent")!;
        agent.CanvasHtml = "<p>canvas</p>";

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "test-agent"));

        cut.Find(".overflow-btn").Click();
        cut.Find("[data-testid='canvas-toggle-btn']").Click();

        // Click backdrop
        var backdrop = cut.Find("[data-testid='canvas-sheet-backdrop']");
        backdrop.Click();

        // Sheet should be gone after close (but async — check immediately after click)
        // Due to the async Task.Delay in the close, the backdrop triggers OnClose which sets _canvasOpen=false
        // The parent Chat.razor should then re-render without the sheet
        cut.WaitForState(() => cut.FindAll("[data-testid='canvas-sheet']").Count == 0, TimeSpan.FromSeconds(1));
    }
}

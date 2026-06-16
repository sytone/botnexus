using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for the mobile "Archive conversation" overflow-menu action: it should
/// appear when a conversation is selected, show a confirm overlay, call
/// <see cref="IAgentInteractionService.ArchiveConversationAsync"/> on confirm, and
/// surface "Close" wording for virtual (cron) sessions.
/// </summary>
public sealed class MobileArchiveConversationTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IClientStateStore _store;
    private readonly IPortalLoadService _portalLoad;
    private readonly IAgentInteractionService _interaction;
    private readonly AgentState _agentState;

    public MobileArchiveConversationTests()
    {
        _store = Substitute.For<IClientStateStore>();
        _portalLoad = Substitute.For<IPortalLoadService>();
        _interaction = Substitute.For<IAgentInteractionService>();

        _portalLoad.IsReady.Returns(true);
        _portalLoad.IsSignalRConnected.Returns(true);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var convState = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Quarterly plan",
            Status = "Active",
            ActiveSessionId = "session-1",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _agentState = new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Agent 1",
            Emoji = null,
            SessionId = "session-1",
            ActiveConversationId = "conv-1"
        };
        _agentState.Conversations["conv-1"] = convState;

        _store.Agents.Returns(new Dictionary<string, AgentState> { ["agent-1"] = _agentState }.AsReadOnly());
        _store.ActiveAgentId.Returns("agent-1");
        _store.GetAgent("agent-1").Returns(_agentState);
        _store.GetMessages("conv-1").Returns(new List<ChatMessage>().AsReadOnly());
        _store.GetStreamState("conv-1").Returns(new ConversationStreamState());

        _interaction.ArchiveConversationAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);

        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Archive_action_visible_in_menu_when_conversation_selected()
    {
        var cut = _ctx.Render<Chat>();

        cut.Find(".overflow-btn").Click();

        var archiveBtn = cut.Find("[data-testid='archive-conversation-btn']");
        Assert.Contains("Archive conversation", archiveBtn.TextContent);
    }

    [Fact]
    public void Archive_action_hidden_when_no_conversation_selected()
    {
        _agentState.ActiveConversationId = null;
        _store.GetMessages(Arg.Any<string>()).Returns(new List<ChatMessage>().AsReadOnly());

        var cut = _ctx.Render<Chat>();

        cut.Find(".overflow-btn").Click();

        Assert.Empty(cut.FindAll("[data-testid='archive-conversation-btn']"));
    }

    [Fact]
    public void Archive_action_shows_confirm_overlay()
    {
        var cut = _ctx.Render<Chat>();

        cut.Find(".overflow-btn").Click();
        cut.Find("[data-testid='archive-conversation-btn']").Click();

        var confirmBtn = cut.Find("[data-testid='archive-confirm-btn']");
        Assert.Equal("Archive", confirmBtn.TextContent.Trim());
        Assert.Contains("Quarterly plan", cut.Find(".reset-confirm-overlay").TextContent);
    }

    [Fact]
    public async Task Archive_confirm_calls_ArchiveConversationAsync()
    {
        var cut = _ctx.Render<Chat>();

        cut.Find(".overflow-btn").Click();
        cut.Find("[data-testid='archive-conversation-btn']").Click();
        cut.Find("[data-testid='archive-confirm-btn']").Click();

        await _interaction.Received(1).ArchiveConversationAsync("agent-1", "conv-1");
    }

    [Fact]
    public async Task Archive_cancel_does_not_call_ArchiveConversationAsync()
    {
        var cut = _ctx.Render<Chat>();

        cut.Find(".overflow-btn").Click();
        cut.Find("[data-testid='archive-conversation-btn']").Click();
        cut.Find("[data-testid='archive-cancel-btn']").Click();

        Assert.Empty(cut.FindAll(".reset-confirm-overlay"));
        await _interaction.DidNotReceive().ArchiveConversationAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public void Archive_overlay_backdrop_click_cancels()
    {
        var cut = _ctx.Render<Chat>();

        cut.Find(".overflow-btn").Click();
        cut.Find("[data-testid='archive-conversation-btn']").Click();
        Assert.NotNull(cut.Find(".reset-confirm-overlay"));

        cut.Find(".reset-confirm-overlay").Click();

        Assert.Empty(cut.FindAll(".reset-confirm-overlay"));
    }

    [Fact]
    public void Virtual_session_uses_close_wording()
    {
        _agentState.Conversations["conv-1"].IsVirtualSession = true;

        var cut = _ctx.Render<Chat>();

        cut.Find(".overflow-btn").Click();

        var archiveBtn = cut.Find("[data-testid='archive-conversation-btn']");
        Assert.Contains("Close conversation", archiveBtn.TextContent);

        archiveBtn.Click();
        var confirmBtn = cut.Find("[data-testid='archive-confirm-btn']");
        Assert.Equal("Close", confirmBtn.TextContent.Trim());
        Assert.Contains("reopen", cut.Find(".reset-confirm-overlay").TextContent);
    }
}

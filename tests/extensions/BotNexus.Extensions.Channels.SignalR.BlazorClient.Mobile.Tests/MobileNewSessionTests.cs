using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for issue #983: mobile "New Session" button should show a confirm dialog,
/// call ResetSessionAsync on confirm, and scroll to bottom afterwards.
/// </summary>
public sealed class MobileNewSessionTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IClientStateStore _store;
    private readonly IPortalLoadService _portalLoad;
    private readonly IAgentInteractionService _interaction;

    public MobileNewSessionTests()
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
            Title = "Test",
            Status = "Active",
            ActiveSessionId = "session-1",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var agentState = new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Agent 1",
            Emoji = null,
            SessionId = "session-1",
            ActiveConversationId = "conv-1"
        };
        agentState.Conversations["conv-1"] = convState;

        _store.Agents.Returns(new Dictionary<string, AgentState> { ["agent-1"] = agentState }.AsReadOnly());
        _store.ActiveAgentId.Returns("agent-1");
        _store.GetAgent("agent-1").Returns(agentState);
        _store.GetMessages("conv-1").Returns(new List<ChatMessage>().AsReadOnly());
        _store.GetStreamState("conv-1").Returns(new ConversationStreamState());

        _interaction.ResetSessionAsync(Arg.Any<string>()).Returns(Task.CompletedTask);

        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(new BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services.MobileHubTuningOptions());
        _ctx.Services.AddSingleton(_interaction);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void NewSession_button_shows_confirm_dialog()
    {
        var cut = _ctx.Render<Chat>();

        // Open the overflow menu
        cut.Find(".overflow-btn").Click();

        // Click "New session" button
        cut.Find("[data-testid='new-session-btn']").Click();

        // Confirm overlay should now be visible
        var overlay = cut.Find(".reset-confirm-overlay");
        Assert.NotNull(overlay);
        var confirmBtn = cut.Find("[data-testid='new-session-confirm-btn']");
        Assert.NotNull(confirmBtn);
    }

    [Fact]
    public async Task NewSession_confirm_calls_ResetSessionAsync()
    {
        var cut = _ctx.Render<Chat>();

        // Open menu and click "New session"
        cut.Find(".overflow-btn").Click();
        cut.Find("[data-testid='new-session-btn']").Click();

        // Click confirm
        cut.Find("[data-testid='new-session-confirm-btn']").Click();

        // ResetSessionAsync should have been called with the active agent
        await _interaction.Received(1).ResetSessionAsync("agent-1");
    }

    [Fact]
    public void NewSession_cancel_hides_dialog()
    {
        var cut = _ctx.Render<Chat>();

        // Open menu and click "New session"
        cut.Find(".overflow-btn").Click();
        cut.Find("[data-testid='new-session-btn']").Click();

        // Verify overlay is shown
        Assert.NotNull(cut.Find(".reset-confirm-overlay"));

        // Click cancel
        cut.Find(".cancel-btn").Click();

        // Overlay should be gone
        Assert.Empty(cut.FindAll(".reset-confirm-overlay"));
    }

    [Fact]
    public void NewSession_overlay_backdrop_click_cancels()
    {
        var cut = _ctx.Render<Chat>();

        // Open menu and click "New session"
        cut.Find(".overflow-btn").Click();
        cut.Find("[data-testid='new-session-btn']").Click();

        // Verify overlay is shown
        Assert.NotNull(cut.Find(".reset-confirm-overlay"));

        // Click the backdrop (the overlay div itself)
        cut.Find(".reset-confirm-overlay").Click();

        // Overlay should be gone
        Assert.Empty(cut.FindAll(".reset-confirm-overlay"));
    }

    [Fact]
    public void NewSession_scrolls_to_bottom_after_reset()
    {
        var cut = _ctx.Render<Chat>();

        // Open menu and click "New session"
        cut.Find(".overflow-btn").Click();
        cut.Find("[data-testid='new-session-btn']").Click();

        // Click confirm to trigger DoNewSession
        cut.Find("[data-testid='new-session-confirm-btn']").Click();

        // Verify that JS interop for forceScrollToBottom was invoked
        // (first call is from OnAfterRenderAsync, subsequent from DoNewSession)
        var invocations = _ctx.JSInterop.Invocations
            .Where(i => i.Identifier == "chatScroll.forceScrollToBottom")
            .ToList();

        // At least 2: one from first render, one from DoNewSession
        Assert.True(invocations.Count >= 2,
            $"Expected at least 2 forceScrollToBottom calls but got {invocations.Count}");
    }
}

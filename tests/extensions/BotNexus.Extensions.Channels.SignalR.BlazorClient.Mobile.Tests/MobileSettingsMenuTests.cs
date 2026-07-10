using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for issue #1615 (config-parity PBI 6/6 of #1579): the mobile overflow menu must expose a
/// "Settings" entry that navigates to the schema-driven mobile Settings page. The menu previously had
/// only New session / New conversation / Canvas / Archive; Settings is the new payoff entry point.
/// </summary>
public sealed class MobileSettingsMenuTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IClientStateStore _store;
    private readonly IPortalLoadService _portalLoad;
    private readonly IAgentInteractionService _interaction;

    public MobileSettingsMenuTests()
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

        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(new BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services.MobileHubTuningOptions());
        _ctx.Services.AddSingleton(_interaction);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Overflow_menu_has_settings_entry()
    {
        var cut = _ctx.Render<Chat>();

        // Open the overflow menu (currently: New session / New conversation / Canvas / Archive).
        cut.Find(".overflow-btn").Click();

        // The new "Settings" entry must be present.
        var settings = cut.Find("[data-testid='settings-btn']");
        Assert.NotNull(settings);
    }

    [Fact]
    public void Settings_entry_navigates_to_settings_page()
    {
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        var cut = _ctx.Render<Chat>();

        cut.Find(".overflow-btn").Click();
        cut.Find("[data-testid='settings-btn']").Click();

        // Clicking Settings routes the mobile client to the schema-driven Settings page.
        Assert.EndsWith("settings", nav.Uri);
    }
}

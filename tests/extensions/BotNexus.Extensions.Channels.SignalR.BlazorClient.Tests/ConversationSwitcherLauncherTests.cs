using AngleSharp.Dom;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// The switcher's entry points. The control itself lives in the chat header as a caret beside the
/// conversation title — discoverable once you know it is there, invisible if you do not, which is
/// how a shipped search feature can read as absent. The sidebar box and the Cmd/Ctrl-K shortcut
/// both open it through <see cref="IConversationSwitcherLauncher"/>, since neither owns it.
/// </summary>
public sealed class ConversationSwitcherLauncherTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly ConversationSwitcherLauncher _launcher = new();

    public ConversationSwitcherLauncherTests()
    {
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _ctx.Services.AddSingleton<IConversationSwitcherLauncher>(_launcher);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static ConversationSummaryDto Conversation(string agentId, string id, string title) => new(
        ConversationId: id, AgentId: agentId, Title: title, IsDefault: false, Status: "Active",
        ActiveSessionId: null, BindingCount: 0, CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

    private void SeedTwoAgents()
    {
        _store.SeedAgents([new AgentSummary("agent-1", "Alpha"), new AgentSummary("agent-2", "Beta")]);
        _store.SeedConversations("agent-1", [Conversation("agent-1", "c-1", "Mine")]);
        _store.SeedConversations("agent-2", [Conversation("agent-2", "c-2", "Theirs")]);
        _store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);
    }

    private IRenderedComponent<ConversationSwitcher> RenderFor(string agentId) =>
        _ctx.Render<ConversationSwitcher>(p => p
            .Add(c => c.AgentId, agentId)
            .Add(c => c.ActiveConversationId, "c-1"));

    private static IReadOnlyList<IElement> Panel(IRenderedComponent<ConversationSwitcher> cut) =>
        cut.FindAll("[data-testid='conversation-switcher-panel']");

    // ---- the launcher itself ---------------------------------------------------------------

    [Fact]
    public void RequestRaisesTheEvent()
    {
        var raised = 0;
        _launcher.Requested += () => raised++;

        _launcher.Request();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void RequestWithNoSubscribersDoesNotThrow()
    {
        // The layout can raise a request before any chat panel has mounted.
        var launcher = new ConversationSwitcherLauncher();

        launcher.Request();
    }

    [Fact]
    public void UnsubscribingStopsDelivery()
    {
        var raised = 0;
        void Handler() => raised++;

        _launcher.Requested += Handler;
        _launcher.Requested -= Handler;
        _launcher.Request();

        Assert.Equal(0, raised);
    }

    // ---- the switcher's response -----------------------------------------------------------

    [Fact]
    public void ARequestOpensTheSwitcherForTheActiveAgent()
    {
        SeedTwoAgents();
        var cut = RenderFor("agent-1");
        Assert.Empty(Panel(cut));

        cut.InvokeAsync(() => _launcher.Request());

        Assert.NotEmpty(Panel(cut));
    }

    [Fact]
    public void ARequestDoesNotOpenSwitchersForOtherAgents()
    {
        // Home renders one panel per agent and hides the inactive ones with CSS rather than
        // unmounting them, so every switcher hears the request. Only the visible one may open.
        SeedTwoAgents();
        var inactive = RenderFor("agent-2");

        inactive.InvokeAsync(() => _launcher.Request());

        Assert.Empty(Panel(inactive));
    }

    [Fact]
    public void ASecondRequestWhileOpenLeavesItOpen()
    {
        // The shortcut is a request to open, not a toggle: pressing it twice must not close the
        // panel the user just asked for.
        SeedTwoAgents();
        var cut = RenderFor("agent-1");

        cut.InvokeAsync(() => _launcher.Request());
        cut.InvokeAsync(() => _launcher.Request());

        Assert.NotEmpty(Panel(cut));
    }

    [Fact]
    public void TheHeaderCaretStillToggles()
    {
        // Splitting Open out of Toggle must not cost the caret its close-on-second-click.
        SeedTwoAgents();
        var cut = RenderFor("agent-1");
        var trigger = cut.Find("[data-testid='conversation-switcher-trigger']");

        trigger.Click();
        Assert.NotEmpty(Panel(cut));

        cut.Find("[data-testid='conversation-switcher-trigger']").Click();
        Assert.Empty(Panel(cut));
    }

    [Fact]
    public void ADisposedSwitcherNoLongerRespondsToRequests()
    {
        // The launcher outlives any one panel; a leaked subscription would raise into a disposed
        // component on every later shortcut press.
        SeedTwoAgents();
        var cut = RenderFor("agent-1");
        cut.Instance.Dispose();

        var ex = Record.Exception(() => _launcher.Request());

        Assert.Null(ex);
    }
}

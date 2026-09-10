using AngleSharp.Dom;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// The mobile shell's cross-agent conversation search. Mobile had a native <c>&lt;select&gt;</c>
/// picker with no text filter and no reach past the active agent; this adds both, beside that
/// picker rather than in place of it.
/// </summary>
public sealed class MobileConversationSearchTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly IAgentInteractionService _interaction = Substitute.For<IAgentInteractionService>();

    public MobileConversationSearchTests()
    {
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static ConversationSummaryDto Conversation(string agentId, string id, string title, string status = "Active") => new(
        ConversationId: id, AgentId: agentId, Title: title, IsDefault: false, Status: status,
        ActiveSessionId: null, BindingCount: 0, CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

    private void SeedTwoAgents(ConversationSummaryDto[] mine, ConversationSummaryDto[] theirs)
    {
        _store.SeedAgents([new AgentSummary("agent-1", "Alpha"), new AgentSummary("agent-2", "Beta")]);
        _store.SeedConversations("agent-1", mine);
        _store.SeedConversations("agent-2", theirs);
        _store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);
    }

    private IRenderedComponent<MobileConversationSearch> Render() =>
        _ctx.Render<MobileConversationSearch>(p => p.Add(c => c.AgentId, "agent-1"));

    private static IElement Trigger(IRenderedComponent<MobileConversationSearch> cut) =>
        cut.Find("[data-testid='mobile-conversation-search-btn']");

    private static IElement Input(IRenderedComponent<MobileConversationSearch> cut) =>
        cut.Find("[data-testid='mobile-conversation-search-input']");

    private static IReadOnlyList<IElement> Rows(IRenderedComponent<MobileConversationSearch> cut) =>
        cut.FindAll("[data-testid='mobile-conversation-search-row']");

    private static void Open(IRenderedComponent<MobileConversationSearch> cut) => Trigger(cut).Click();

    [Fact]
    public void OverlayIsClosedUntilTheSearchButtonIsTapped()
    {
        SeedTwoAgents([Conversation("agent-1", "c-1", "Mine")], [Conversation("agent-2", "c-2", "Theirs")]);

        var cut = Render();

        Assert.Empty(cut.FindAll("[data-testid='mobile-conversation-search-overlay']"));
        Assert.NotNull(Trigger(cut));
    }

    [Fact]
    public void TappingSearchOpensTheOverlayWithAnEmptyQueryHint()
    {
        // With no query the overlay must not dump every conversation of every agent onto a phone.
        SeedTwoAgents([Conversation("agent-1", "c-1", "Mine")], [Conversation("agent-2", "c-2", "Theirs")]);

        var cut = Render();
        Open(cut);

        Assert.NotNull(cut.Find("[data-testid='mobile-conversation-search-overlay']"));
        Assert.NotNull(cut.Find("[data-testid='mobile-conversation-search-hint']"));
        Assert.Empty(Rows(cut));
    }

    [Fact]
    public void TypingFindsTheCurrentAgentsConversations()
    {
        SeedTwoAgents(
            [Conversation("agent-1", "c-1", "Deploy notes"), Conversation("agent-1", "c-3", "Unrelated")],
            [Conversation("agent-2", "c-2", "Deploy runbook")]);

        var cut = Render();
        Open(cut);
        Input(cut).Input("deploy notes");

        var rows = Rows(cut);
        Assert.Single(rows);
        Assert.Equal("c-1", rows[0].GetAttribute("data-conversation-id"));
    }

    [Fact]
    public void TypingReachesOtherAgentsAndLabelsThem()
    {
        SeedTwoAgents(
            [Conversation("agent-1", "c-1", "Deploy notes")],
            [Conversation("agent-2", "c-2", "Deploy runbook")]);

        var cut = Render();
        Open(cut);
        Input(cut).Input("deploy");

        var foreign = Rows(cut).Single(r => r.GetAttribute("data-conversation-id") == "c-2");
        Assert.Equal("agent-2", foreign.GetAttribute("data-agent-id"));
        Assert.Contains("Beta", foreign.TextContent, StringComparison.Ordinal);
        Assert.Contains(ConversationSwitcherModel.OtherAgentsLabel,
            cut.FindAll(".conv-search-group").Select(e => e.TextContent.Trim()));
    }

    [Fact]
    public void OnlyOtherAgentRowsCarryAnAgentLabel()
    {
        SeedTwoAgents(
            [Conversation("agent-1", "c-1", "Deploy notes")],
            [Conversation("agent-2", "c-2", "Deploy runbook")]);

        var cut = Render();
        Open(cut);
        Input(cut).Input("deploy");

        var own = Rows(cut).Single(r => r.GetAttribute("data-conversation-id") == "c-1");
        Assert.Empty(own.QuerySelectorAll("[data-testid='mobile-conversation-search-agent-label']"));
    }

    [Fact]
    public void SelectingAnotherAgentsConversationSwitchesAgentAndNavigates()
    {
        SeedTwoAgents(
            [Conversation("agent-1", "c-1", "Deploy notes")],
            [Conversation("agent-2", "c-2", "Deploy runbook")]);

        var cut = Render();
        Open(cut);
        Input(cut).Input("deploy");
        Rows(cut).Single(r => r.GetAttribute("data-conversation-id") == "c-2").Click();

        _interaction.Received(1).SelectConversationAsync("agent-2", "c-2");
        _interaction.DidNotReceive().SelectConversationAsync("agent-1", "c-2");
        Assert.Equal("agent-2", _store.ActiveAgentId);

        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith("agent/agent-2/conversation/c-2", nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectingAConversationClosesTheOverlay()
    {
        SeedTwoAgents([Conversation("agent-1", "c-1", "Deploy notes")], []);

        var cut = Render();
        Open(cut);
        Input(cut).Input("deploy");
        Rows(cut)[0].Click();

        Assert.Empty(cut.FindAll("[data-testid='mobile-conversation-search-overlay']"));
    }

    [Fact]
    public void SelectingRaisesOnSelectedSoThePageCanScroll()
    {
        // Chat.razor scrolls the transcript to the latest message after its own picker changes; the
        // callback is how this component gets the same behaviour without owning the page's JS.
        SeedTwoAgents([Conversation("agent-1", "c-1", "Deploy notes")], []);

        var raised = 0;
        var cut = _ctx.Render<MobileConversationSearch>(p => p
            .Add(c => c.AgentId, "agent-1")
            .Add(c => c.OnSelected, EventCallback.Factory.Create(this, () => raised++)));

        Trigger(cut).Click();
        Input(cut).Input("deploy");
        Rows(cut)[0].Click();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void EnterOpensTheFirstResult()
    {
        // A phone's soft keyboard offers "go", not arrow keys.
        SeedTwoAgents(
            [Conversation("agent-1", "c-1", "Deploy notes")],
            [Conversation("agent-2", "c-2", "Deploy runbook")]);

        var cut = Render();
        Open(cut);
        Input(cut).Input("deploy");

        var first = Rows(cut)[0];
        var expectedAgent = first.GetAttribute("data-agent-id")!;
        var expectedConv = first.GetAttribute("data-conversation-id")!;

        Input(cut).KeyDown(new KeyboardEventArgs { Key = "Enter" });

        _interaction.Received(1).SelectConversationAsync(expectedAgent, expectedConv);
    }

    [Fact]
    public void EnterOnAnEmptyResultSelectsNothing()
    {
        SeedTwoAgents([Conversation("agent-1", "c-1", "Deploy notes")], []);

        var cut = Render();
        Open(cut);
        Input(cut).Input("zzzzz");
        Input(cut).KeyDown(new KeyboardEventArgs { Key = "Enter" });

        _interaction.DidNotReceive().SelectConversationAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public void AQueryMatchingNothingShowsTheEmptyState()
    {
        SeedTwoAgents([Conversation("agent-1", "c-1", "Deploy notes")], [Conversation("agent-2", "c-2", "Runbook")]);

        var cut = Render();
        Open(cut);
        Input(cut).Input("zzzzz");

        Assert.Empty(Rows(cut));
        Assert.NotNull(cut.Find("[data-testid='mobile-conversation-search-empty']"));
    }

    [Fact]
    public void CancelClosesTheOverlay()
    {
        SeedTwoAgents([Conversation("agent-1", "c-1", "Mine")], []);

        var cut = Render();
        Open(cut);
        cut.Find("[data-testid='mobile-conversation-search-cancel']").Click();

        Assert.Empty(cut.FindAll("[data-testid='mobile-conversation-search-overlay']"));
    }

    [Fact]
    public void EscapeClosesTheOverlay()
    {
        SeedTwoAgents([Conversation("agent-1", "c-1", "Mine")], []);

        var cut = Render();
        Open(cut);
        Input(cut).KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.Empty(cut.FindAll("[data-testid='mobile-conversation-search-overlay']"));
    }

    [Fact]
    public void ReopeningStartsFromAnEmptyQuery()
    {
        SeedTwoAgents([Conversation("agent-1", "c-1", "Deploy notes")], []);

        var cut = Render();
        Open(cut);
        Input(cut).Input("deploy");
        cut.Find("[data-testid='mobile-conversation-search-cancel']").Click();
        Open(cut);

        Assert.NotNull(cut.Find("[data-testid='mobile-conversation-search-hint']"));
        Assert.Empty(Rows(cut));
    }

    [Fact]
    public void ArchivedConversationsAreNotReachable()
    {
        SeedTwoAgents(
            [Conversation("agent-1", "c-1", "Deploy live")],
            [Conversation("agent-2", "c-2", "Deploy archived", status: "Archived")]);

        var cut = Render();
        Open(cut);
        Input(cut).Input("deploy");

        var rows = Rows(cut);
        Assert.Single(rows);
        Assert.Equal("c-1", rows[0].GetAttribute("data-conversation-id"));
    }
}

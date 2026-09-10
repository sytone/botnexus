using AngleSharp.Dom;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// The chat header's type-to-filter conversation switcher. The sidebar is a filing system -
/// four groups plus user sections, three of them collapsible, and an activity filter that can hide
/// a row - so reaching a conversation there means first remembering where it was filed. These tests
/// pin the finding path: open, type, arrow, Enter.
/// </summary>
public sealed class ConversationSwitcherTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly IAgentInteractionService _interaction = Substitute.For<IAgentInteractionService>();

    public ConversationSwitcherTests()
    {
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(_interaction);
        // CronApiClient is deliberately NOT registered: the component resolves it optionally, and
        // this fixture is also the regression test for that (a hard dependency here would have
        // broken sixteen existing ChatPanel fixtures the same way).
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static ConversationSummaryDto Conversation(
        string id,
        string title,
        bool isDefault = false,
        string status = "Active",
        string source = "Channel",
        string kind = "HumanAgent",
        string visibility = "UserFacing",
        bool isPinned = false) => new(
            ConversationId: id,
            AgentId: "agent-1",
            Title: title,
            IsDefault: isDefault,
            Status: status,
            ActiveSessionId: null,
            BindingCount: 0,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Kind: kind,
            Source: source,
            Visibility: visibility,
            IsPinned: isPinned);

    private void Seed(params ConversationSummaryDto[] conversations)
    {
        _store.SeedAgents([new AgentSummary("agent-1", "Alpha")]);
        _store.SeedConversations("agent-1", conversations);
        _store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);
    }

    /// <summary>Seeds two agents so the cross-agent path has somewhere to search.</summary>
    private void SeedTwoAgents(ConversationSummaryDto[] mine, ConversationSummaryDto[] theirs)
    {
        _store.SeedAgents([new AgentSummary("agent-1", "Alpha"), new AgentSummary("agent-2", "Beta")]);
        _store.SeedConversations("agent-1", mine);
        _store.SeedConversations("agent-2", theirs);
        _store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);
    }

    private static ConversationSummaryDto ConversationFor(string agentId, string id, string title) => new(
        ConversationId: id, AgentId: agentId, Title: title, IsDefault: false, Status: "Active",
        ActiveSessionId: null, BindingCount: 0, CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

    private IRenderedComponent<ConversationSwitcher> Render(string? active = "c-1") =>
        _ctx.Render<ConversationSwitcher>(p => p
            .Add(c => c.AgentId, "agent-1")
            .Add(c => c.ActiveConversationId, active));

    private static IElement Trigger(IRenderedComponent<ConversationSwitcher> cut) =>
        cut.Find("[data-testid='conversation-switcher-trigger']");

    private static IElement Input(IRenderedComponent<ConversationSwitcher> cut) =>
        cut.Find("[data-testid='conversation-switcher-input']");

    private static IReadOnlyList<IElement> Rows(IRenderedComponent<ConversationSwitcher> cut) =>
        cut.FindAll("[data-testid='conversation-switcher-row']");

    private static void Open(IRenderedComponent<ConversationSwitcher> cut) => Trigger(cut).Click();

    private static string IdOf(IElement row) => row.GetAttribute("data-conversation-id")!;

    /// <summary>
    /// Row ids in the order they are RENDERED. The keyboard tests derive their expectations from
    /// this rather than from the order conversations were seeded in: the highlight is an index into
    /// the rendered list, so hard-coding a seed order tests an assumption about dictionary
    /// enumeration instead of testing the keyboard.
    /// </summary>
    private static List<string> RenderedIds(IRenderedComponent<ConversationSwitcher> cut) =>
        Rows(cut).Select(IdOf).ToList();

    /// <summary>The id of the row currently carrying the visible highlight.</summary>
    private static string HighlightedId(IRenderedComponent<ConversationSwitcher> cut) =>
        IdOf(Rows(cut).Single(r => r.ClassList.Contains("highlighted")));

    [Fact]
    public void PanelIsClosedUntilTheTriggerIsClicked()
    {
        Seed(Conversation("c-1", "Alpha"));

        var cut = Render();

        Assert.Empty(cut.FindAll("[data-testid='conversation-switcher-panel']"));
        Assert.Equal("false", Trigger(cut).GetAttribute("aria-expanded"));
    }

    [Fact]
    public void ClickingTheTriggerOpensThePanelWithASearchBox()
    {
        Seed(Conversation("c-1", "Alpha"));

        var cut = Render();
        Open(cut);

        Assert.NotNull(cut.Find("[data-testid='conversation-switcher-panel']"));
        Assert.NotNull(Input(cut));
        Assert.Equal("true", Trigger(cut).GetAttribute("aria-expanded"));
    }

    [Fact]
    public void OpenPanelListsEveryReachableConversation()
    {
        Seed(
            Conversation("c-1", "Alpha"),
            Conversation("c-2", "Beta"),
            Conversation("c-3", "Gamma"));

        var cut = Render();
        Open(cut);

        Assert.Equal(3, Rows(cut).Count);
    }

    [Fact]
    public void TypingFiltersTheListToMatchingTitles()
    {
        Seed(
            Conversation("c-1", "Daily Cost Monitor Report"),
            Conversation("c-2", "Skill Review"),
            Conversation("c-3", "Exact Reply Instruction Followed"));

        var cut = Render();
        Open(cut);
        Input(cut).Input("skill");

        var rows = Rows(cut);
        Assert.Single(rows);
        Assert.Equal("c-2", rows[0].GetAttribute("data-conversation-id"));
    }

    [Fact]
    public void QueryMatchingNothingShowsTheEmptyState()
    {
        Seed(Conversation("c-1", "Alpha"));

        var cut = Render();
        Open(cut);
        Input(cut).Input("zzzzz");

        Assert.Empty(Rows(cut));
        Assert.NotNull(cut.Find("[data-testid='conversation-switcher-empty']"));
    }

    [Fact]
    public void RowsAreGroupedUnderTheSharedGroupLabels()
    {
        Seed(
            Conversation("c-pinned", "Pinned One", isPinned: true),
            Conversation("c-normal", "Normal One"),
            Conversation("c-cron", "Nightly Job", source: "Cron"));

        var cut = Render();
        Open(cut);

        var labels = cut.FindAll("[data-testid='conversation-switcher-panel'] .conversation-switcher-group-label")
            .Select(e => e.TextContent.Trim())
            .ToList();

        Assert.Equal(
            new List<string>
            {
                PortalConversationGrouping.PinnedLabel,
                PortalConversationGrouping.ConversationsLabel,
                PortalConversationGrouping.ScheduledLabel,
            },
            labels);
    }

    [Fact]
    public void SelectingARowSwitchesTheConversationAndNavigates()
    {
        Seed(Conversation("c-1", "Alpha"), Conversation("c-2", "Beta"));

        var cut = Render(active: "c-1");
        Open(cut);
        Rows(cut).Single(r => r.GetAttribute("data-conversation-id") == "c-2").Click();

        _interaction.Received(1).SelectConversationAsync("agent-1", "c-2");

        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith("agent/agent-1/conversation/c-2", nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectingARowClosesThePanel()
    {
        Seed(Conversation("c-1", "Alpha"), Conversation("c-2", "Beta"));

        var cut = Render();
        Open(cut);
        Rows(cut).Single(r => r.GetAttribute("data-conversation-id") == "c-2").Click();

        Assert.Empty(cut.FindAll("[data-testid='conversation-switcher-panel']"));
    }

    [Fact]
    public void ArrowDownThenEnterOpensTheRowBelowTheActiveOne()
    {
        // The panel opens highlighting the CURRENT conversation, so exactly one ArrowDown must land
        // on its neighbour - this is the whole keyboard path in one assertion.
        Seed(Conversation("c-1", "Alpha"), Conversation("c-2", "Beta"));

        var cut = Render(active: "c-1");
        Open(cut);

        var ids = RenderedIds(cut);
        var expected = ids[(ids.IndexOf("c-1") + 1) % ids.Count];

        Input(cut).KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        Assert.Equal(expected, HighlightedId(cut));

        Input(cut).KeyDown(new KeyboardEventArgs { Key = "Enter" });

        _interaction.Received(1).SelectConversationAsync("agent-1", expected);
    }

    [Fact]
    public void EnterWithoutArrowingReopensTheConversationAlreadyOnScreen()
    {
        // Opening highlights where the user already is, so a stray Enter must not jump them
        // somewhere else.
        Seed(Conversation("c-1", "Alpha"), Conversation("c-2", "Beta"));

        var cut = Render(active: "c-2");
        Open(cut);
        Input(cut).KeyDown(new KeyboardEventArgs { Key = "Enter" });

        _interaction.Received(1).SelectConversationAsync("agent-1", "c-2");
    }

    [Fact]
    public void ArrowDownWrapsFromTheLastRowToTheFirst()
    {
        Seed(Conversation("c-1", "Alpha"), Conversation("c-2", "Beta"));

        var cut = Render(active: "c-2");
        Open(cut);

        var ids = RenderedIds(cut);
        // Park the highlight on the LAST row whatever its id, then step past the end.
        Input(cut).KeyDown(new KeyboardEventArgs { Key = "End" });
        Assert.Equal(ids[^1], HighlightedId(cut));

        Input(cut).KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        Assert.Equal(ids[0], HighlightedId(cut));

        Input(cut).KeyDown(new KeyboardEventArgs { Key = "Enter" });

        _interaction.Received(1).SelectConversationAsync("agent-1", ids[0]);
    }

    [Fact]
    public void ArrowUpWrapsFromTheFirstRowToTheLast()
    {
        Seed(Conversation("c-1", "Alpha"), Conversation("c-2", "Beta"));

        var cut = Render(active: "c-1");
        Open(cut);

        var ids = RenderedIds(cut);
        Input(cut).KeyDown(new KeyboardEventArgs { Key = "Home" });
        Assert.Equal(ids[0], HighlightedId(cut));

        Input(cut).KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        Assert.Equal(ids[^1], HighlightedId(cut));

        Input(cut).KeyDown(new KeyboardEventArgs { Key = "Enter" });

        _interaction.Received(1).SelectConversationAsync("agent-1", ids[^1]);
    }

    [Fact]
    public void TypingResetsTheHighlightToTheFirstSurvivingRow()
    {
        // Without the reset the retained index points at whatever now sits at that offset, which
        // after filtering is a different conversation than the one that was highlighted.
        Seed(
            Conversation("c-1", "Alpha"),
            Conversation("c-2", "Beta"),
            Conversation("c-3", "Beta Two"));

        var cut = Render(active: "c-1");
        Open(cut);
        Input(cut).KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        Input(cut).Input("Beta");

        // The invariant is "the highlight is on the first SURVIVING row", which is a statement about
        // the filtered list as rendered - not about which conversation happens to be first.
        var survivors = RenderedIds(cut);
        Assert.Equal(2, survivors.Count);
        Assert.Equal(survivors[0], HighlightedId(cut));

        Input(cut).KeyDown(new KeyboardEventArgs { Key = "Enter" });

        _interaction.Received(1).SelectConversationAsync("agent-1", survivors[0]);
    }

    [Fact]
    public void EnterOnAnEmptyResultSelectsNothing()
    {
        Seed(Conversation("c-1", "Alpha"));

        var cut = Render();
        Open(cut);
        Input(cut).Input("zzzzz");
        Input(cut).KeyDown(new KeyboardEventArgs { Key = "Enter" });

        _interaction.DidNotReceive().SelectConversationAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public void EscapeClosesThePanel()
    {
        Seed(Conversation("c-1", "Alpha"));

        var cut = Render();
        Open(cut);
        Input(cut).KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.Empty(cut.FindAll("[data-testid='conversation-switcher-panel']"));
    }

    [Fact]
    public void ClickingTheBackdropClosesThePanel()
    {
        Seed(Conversation("c-1", "Alpha"));

        var cut = Render();
        Open(cut);
        cut.Find("[data-testid='conversation-switcher-backdrop']").Click();

        Assert.Empty(cut.FindAll("[data-testid='conversation-switcher-panel']"));
    }

    [Fact]
    public void ReopeningStartsFromAnEmptyQuery()
    {
        Seed(Conversation("c-1", "Alpha"), Conversation("c-2", "Beta"));

        var cut = Render();
        Open(cut);
        Input(cut).Input("Alpha");
        Trigger(cut).Click();
        Open(cut);

        Assert.Equal(2, Rows(cut).Count);
    }

    [Fact]
    public void UnattendedConversationsCarryAReadOnlyMarker()
    {
        Seed(
            Conversation("c-1", "Ordinary Chat"),
            Conversation("c-2", "Nightly Job", source: "Cron"));

        var cut = Render();
        Open(cut);

        var cronRow = Rows(cut).Single(r => r.GetAttribute("data-conversation-id") == "c-2");
        var chatRow = Rows(cut).Single(r => r.GetAttribute("data-conversation-id") == "c-1");

        Assert.Contains("Read-only", cronRow.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Read-only", chatRow.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalHiddenConversationsAreNotReachable()
    {
        Seed(
            Conversation("c-1", "Visible"),
            Conversation("internal-1", "Bookkeeping", visibility: "InternalHidden"));

        var cut = Render();
        Open(cut);

        Assert.Single(Rows(cut));
        Assert.Equal("c-1", Rows(cut)[0].GetAttribute("data-conversation-id"));
    }

    [Fact]
    public void ArchivedConversationsAreNotReachable()
    {
        Seed(Conversation("c-1", "Live"), Conversation("c-2", "Old", status: "Archived"));

        var cut = Render();
        Open(cut);

        Assert.Single(Rows(cut));
        Assert.Equal("c-1", Rows(cut)[0].GetAttribute("data-conversation-id"));
    }

    [Fact]
    public void TriggerAnnouncesTheCurrentConversation()
    {
        // The trigger is a bare caret beside the heading, so its accessible name is the only thing
        // that says which conversation it would switch away from.
        Seed(Conversation("c-1", "Alpha"));

        var cut = Render(active: "c-1");

        Assert.Contains("Alpha", Trigger(cut).GetAttribute("aria-label") ?? "", StringComparison.Ordinal);
    }
    // ---- cross-agent search -------------------------------------------------------------------

    [Fact]
    public void OtherAgentsAreNotListedUntilSomethingIsTyped()
    {
        SeedTwoAgents(
            [ConversationFor("agent-1", "c-1", "Mine")],
            [ConversationFor("agent-2", "c-2", "Theirs")]);

        var cut = Render();
        Open(cut);

        Assert.Single(Rows(cut));
        Assert.Empty(cut.FindAll("[data-testid='conversation-switcher-agent-label']"));
    }

    [Fact]
    public void TypingSurfacesAnotherAgentsConversationLabelledWithItsAgent()
    {
        SeedTwoAgents(
            [ConversationFor("agent-1", "c-1", "Deploy notes")],
            [ConversationFor("agent-2", "c-2", "Deploy runbook")]);

        var cut = Render();
        Open(cut);
        Input(cut).Input("deploy");

        var foreign = Rows(cut).Single(r => r.GetAttribute("data-conversation-id") == "c-2");
        Assert.Equal("agent-2", foreign.GetAttribute("data-agent-id"));
        Assert.Contains("Beta", foreign.TextContent, StringComparison.Ordinal);
        Assert.Contains(ConversationSwitcherModel.OtherAgentsLabel,
            cut.FindAll(".conversation-switcher-group-label").Select(e => e.TextContent.Trim()));
    }

    [Fact]
    public void SelectingAnotherAgentsConversationSwitchesAgentAndNavigatesThere()
    {
        SeedTwoAgents(
            [ConversationFor("agent-1", "c-1", "Deploy notes")],
            [ConversationFor("agent-2", "c-2", "Deploy runbook")]);

        var cut = Render();
        Open(cut);
        Input(cut).Input("deploy");
        Rows(cut).Single(r => r.GetAttribute("data-conversation-id") == "c-2").Click();

        // routed to the OWNING agent, not the one the switcher was mounted in
        _interaction.Received(1).SelectConversationAsync("agent-2", "c-2");
        _interaction.DidNotReceive().SelectConversationAsync("agent-1", "c-2");
        Assert.Equal("agent-2", _store.ActiveAgentId);

        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith("agent/agent-2/conversation/c-2", nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void EnterOpensAHighlightedCrossAgentRowOnItsOwnAgent()
    {
        SeedTwoAgents(
            [ConversationFor("agent-1", "c-1", "Deploy notes")],
            [ConversationFor("agent-2", "c-2", "Deploy runbook")]);

        var cut = Render();
        Open(cut);
        Input(cut).Input("deploy");

        // walk the rendered list to the foreign row rather than assuming its index
        var ids = RenderedIds(cut);
        for (var i = 0; i < ids.Count && HighlightedId(cut) != "c-2"; i++)
            Input(cut).KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

        Assert.Equal("c-2", HighlightedId(cut));
        Input(cut).KeyDown(new KeyboardEventArgs { Key = "Enter" });

        _interaction.Received(1).SelectConversationAsync("agent-2", "c-2");
    }

    [Fact]
    public void AQueryMatchingOnlyAnotherAgentStillFindsIt()
    {
        SeedTwoAgents(
            [ConversationFor("agent-1", "c-1", "Nothing relevant")],
            [ConversationFor("agent-2", "c-2", "Gateway restart guide")]);

        var cut = Render();
        Open(cut);
        Input(cut).Input("gateway");

        var rows = Rows(cut);
        Assert.Single(rows);
        Assert.Equal("c-2", rows[0].GetAttribute("data-conversation-id"));
        Assert.Empty(cut.FindAll("[data-testid='conversation-switcher-empty']"));
    }

    [Fact]
    public void TheActiveMarkerNeverLandsOnAnotherAgentsRowWithTheSameId()
    {
        // Two agents can hold conversations with the same id only by coincidence, but if that
        // happens the foreign row must not be painted as the one on screen.
        SeedTwoAgents(
            [ConversationFor("agent-1", "dup", "Deploy mine")],
            [ConversationFor("agent-2", "dup", "Deploy theirs")]);

        var cut = Render(active: "dup");
        Open(cut);
        Input(cut).Input("deploy");

        var foreign = Rows(cut).Single(r => r.GetAttribute("data-agent-id") == "agent-2");
        Assert.Equal("false", foreign.GetAttribute("aria-selected"));
        var own = Rows(cut).Single(r => r.GetAttribute("data-agent-id") == "agent-1");
        Assert.Equal("true", own.GetAttribute("aria-selected"));
    }

    // ── content search hookup (Interface Review P1) ───────────────────────────

    [Fact]
    public async Task Typing_reaches_the_content_search_endpoint_but_only_once_the_query_is_specific()
    {
        // Both halves in one test, and deliberately so: the short query is proved NOT to have
        // fired by the call count when the long one does. Asserting "no call yet" on its own would
        // pass simply by running before the debounce elapsed.
        var handler = new RecordingHandler();
        _ctx.Services.AddSingleton(new HttpClient(handler));
        Seed(Conversation("c-1", "Tuesday standup"));
        var cut = Render();
        Open(cut);

        Input(cut).Input("ga");
        Input(cut).Input("gateway");

        await cut.WaitForAssertionAsync(
            () => handler.Urls.ShouldNotBeEmpty(),
            TimeSpan.FromSeconds(5));

        handler.Urls.Count.ShouldBe(1, "a two-character query must not put a full-text search behind it");
        handler.Urls[0].ShouldContain("/api/conversations/search");
        handler.Urls[0].ShouldContain("q=gateway");
    }

    [Fact]
    public async Task A_conversation_matched_only_by_content_shows_its_snippet()
    {
        // The snippet is the only thing explaining why a row whose title does not contain the
        // query is in the list.
        var handler = new RecordingHandler
        {
            Body = """{"results":[{"conversationId":"c-1","snippet":"the gateway restart lost its pid file"}]}"""
        };
        _ctx.Services.AddSingleton(new HttpClient(handler));
        Seed(Conversation("c-1", "Tuesday standup"));
        var cut = Render();
        Open(cut);

        Input(cut).Input("gateway");

        await cut.WaitForAssertionAsync(
            () => cut.FindAll("[data-testid='conversation-switcher-snippet']").ShouldNotBeEmpty(),
            TimeSpan.FromSeconds(5));

        cut.Find("[data-testid='conversation-switcher-snippet']").TextContent
            .ShouldContain("gateway restart");
    }

    [Fact]
    public async Task A_failed_content_search_leaves_the_title_matches_alone()
    {
        // The title filter has already produced a usable list. Turning that into an error because
        // a supplementary search did not answer would make the control worse than before content
        // search existed.
        var handler = new RecordingHandler { StatusCode = System.Net.HttpStatusCode.InternalServerError };
        _ctx.Services.AddSingleton(new HttpClient(handler));
        Seed(Conversation("c-1", "Skill Review"));
        var cut = Render();
        Open(cut);

        Input(cut).Input("skill");

        await cut.WaitForAssertionAsync(
            () => handler.Urls.ShouldNotBeEmpty(),
            TimeSpan.FromSeconds(5));

        cut.FindAll("[data-testid='conversation-switcher-row']").ShouldNotBeEmpty(
            "a failed content search must not take the title matches down with it");
    }

    [Fact]
    public void Typing_without_a_registered_HttpClient_is_harmless()
    {
        // The switcher is rendered inside ChatPanel, whose fixtures do not register an HttpClient.
        // The client is resolved, not injected, for exactly that reason - the same lesson as
        // CronApiClient above.
        Seed(Conversation("c-1", "Gateway notes"));
        var cut = Render();
        Open(cut);

        Input(cut).Input("gateway");

        cut.FindAll("[data-testid='conversation-switcher-row']").ShouldNotBeEmpty();
    }

    /// <summary>Records the URLs the switcher asks for, and answers with a canned payload.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        public System.Net.HttpStatusCode StatusCode { get; set; } = System.Net.HttpStatusCode.OK;

        public string Body { get; set; } = """{"results":[]}""";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(Uri.UnescapeDataString(request.RequestUri!.ToString()));
            return Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(Body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}

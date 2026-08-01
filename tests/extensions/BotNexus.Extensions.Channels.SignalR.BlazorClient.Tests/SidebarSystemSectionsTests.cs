using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #2122 (re-scoped residual): the sidebar's system sections.
///
/// Everything these tests assert is asserted against the REAL rendered bUnit DOM of
/// <see cref="MainLayout"/> - the group container elements, their header toggles, the count
/// badges on those headers, and which conversation items are actually present in the group body.
///
/// Three behaviours are covered:
///   1. Webhook-originated conversations get their OWN 4th section, rather than being folded in
///      with Scheduled. Membership derives from the existing typed projection
///      (<c>ConversationListGroup.Automated</c>) - there is no second classifier here.
///   2. Pinned and Conversations are collapsible through a real &lt;button&gt; header (so keyboard
///      activation is native), persisting through the same localStorage key pattern the Scheduled
///      group already uses.
///   3. Every group header carries a count badge that stays rendered while the group is collapsed.
/// </summary>
public sealed class SidebarSystemSectionsTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store;

    public SidebarSystemSectionsTests()
    {
        _store = new ClientStateStore();
        var interaction = Substitute.For<IAgentInteractionService>();
        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(false);
        portalLoad.IsLoading.Returns(true);
        portalLoad.LoadError.Returns((string?)null);

        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.ApiBaseUrl.Returns("");
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(interaction);
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(portalLoad);
        _ctx.Services.AddSingleton(new GatewayHubConnection());
        _ctx.Services.AddSingleton(new GatewayInfoService(http, restClient));
        _ctx.Services.AddSingleton(Substitute.For<IUpdateStatusService>());
        var prefs = Substitute.For<IPortalPreferencesService>();
        prefs.Current.Returns(new PortalPreferences());
        _ctx.Services.AddSingleton(prefs);
        _ctx.Services.AddSingleton(restClient);
        _ctx.Services.AddSingleton(Substitute.For<IChannelErrorReporter>());
        _ctx.Services.AddSingleton(http);
        _ctx.Services.AddSingleton(new ExtensionFeatureService(restClient));
        _ctx.Services.AddSingleton(new CronApiClient(http));
        _ctx.Services.AddSingleton(new SectionsApiClient(http));
        _ctx.Services.AddSingleton(new ToolsApiClient(http));
        _ctx.Services.AddStubNavOrderApiClient();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<MainLayout> RenderLayout() =>
        _ctx.Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(_ => { })));

    private void Seed(params ConversationSummaryDto[] conversations)
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", conversations);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);
    }

    // Seeds a persisted collapse flag so the rendered output is deterministic. Under the Loose JS
    // mock an unseeded localStorage read returns null, and MainLayout.OnAfterRenderAsync applies
    // that on an async continuation which can race a toggle click (the same flake the Scheduled
    // group tests already guard against with an explicit seed).
    private void SeedCollapsed(string key, bool collapsed) =>
        _ctx.JSInterop.Setup<string?>("localStorage.getItem", key).SetResult(collapsed ? "true" : "false");

    private static ConversationSummaryDto Conv(string id, string title, string source = "Channel", bool pinned = false) =>
        new(id, "a-1", title, false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "HumanAgent", source, IsPinned: pinned);

    // ---------------------------------------------------------------- webhooks as a 4th section

    /// <summary>
    /// A server-stamped <c>Source=Webhook</c> conversation renders inside its own Automated group -
    /// not in Scheduled (where the enum doc comment said it was folded) and not in Conversations.
    /// </summary>
    [Fact]
    public void WebhookConversations_RenderInTheirOwnAutomatedGroup()
    {
        SeedCollapsed("botnexus-webhook-collapsed", false);
        SeedCollapsed("botnexus-cron-collapsed", false);

        Seed(
            Conv("c-hook", "Hook Run", source: "Webhook"),
            Conv("c-cron", "Cron Run", source: "Cron"),
            Conv("c-normal", "Normal Chat"));

        var cut = RenderLayout();

        cut.WaitForAssertion(() =>
            Assert.Contains("Hook Run", cut.Find("[data-testid='conversation-group-automated']").TextContent));

        Assert.DoesNotContain("Hook Run", cut.Find("[data-testid='conversation-group-scheduled']").TextContent);
        Assert.DoesNotContain("Hook Run", cut.Find("[data-testid='conversation-group-conversations']").TextContent);
        // And the scheduled group keeps only the cron run.
        Assert.Contains("Cron Run", cut.Find("[data-testid='conversation-group-scheduled']").TextContent);
    }

    /// <summary>The Automated group is not rendered at all when no webhook conversation exists.</summary>
    [Fact]
    public void AutomatedGroup_NotRendered_WhenNoWebhookConversations()
    {
        Seed(Conv("c-normal", "Normal Chat"));

        var cut = RenderLayout();

        Assert.Empty(cut.FindAll("[data-testid='conversation-group-automated']"));
    }

    /// <summary>First-time user (no persisted key): the Automated group starts collapsed.</summary>
    [Fact]
    public void AutomatedGroup_CollapsedByDefault_ForFirstTimeUser()
    {
        Seed(Conv("c-hook", "Hook Run", source: "Webhook"));

        var cut = RenderLayout();

        var group = cut.Find("[data-testid='conversation-group-automated']");
        Assert.Empty(group.QuerySelectorAll("[data-testid='conversation-list-item']"));
        Assert.DoesNotContain("Hook Run", group.TextContent);
    }

    /// <summary>
    /// The Automated header is a real &lt;button&gt; (native keyboard activation) and toggling it
    /// reveals the webhook conversation in the group body.
    /// </summary>
    [Fact]
    public void AutomatedGroup_ToggleIsAButton_AndExpandsOnActivation()
    {
        Seed(Conv("c-hook", "Hook Run", source: "Webhook"));

        var cut = RenderLayout();

        var toggle = cut.Find("[data-testid='webhook-group-toggle']");
        Assert.Equal("BUTTON", toggle.TagName);

        cut.InvokeAsync(() => cut.Find("[data-testid='webhook-group-toggle']").Click());

        cut.WaitForAssertion(() =>
            Assert.Contains("Hook Run", cut.Find("[data-testid='conversation-group-automated']").TextContent));
    }

    // ------------------------------------------------- pinned / conversations become collapsible

    /// <summary>Pinned gets a real &lt;button&gt; header toggle; activating it hides its items.</summary>
    [Fact]
    public void PinnedGroup_ToggleIsAButton_AndCollapsesOnActivation()
    {
        Seed(Conv("c-pin", "Pinned Chat", pinned: true), Conv("c-normal", "Normal Chat"));

        var cut = RenderLayout();

        var toggle = cut.Find("[data-testid='pinned-group-toggle']");
        Assert.Equal("BUTTON", toggle.TagName);
        // Pinned defaults to expanded - it is the user's own explicit shortlist.
        Assert.Contains("Pinned Chat", cut.Find("[data-testid='conversation-group-pinned']").TextContent);

        cut.InvokeAsync(() => cut.Find("[data-testid='pinned-group-toggle']").Click());

        cut.WaitForAssertion(() =>
            Assert.DoesNotContain("Pinned Chat", cut.Find("[data-testid='conversation-group-pinned']").TextContent));
    }

    /// <summary>Pinned collapse state is restored from the persisted localStorage key.</summary>
    [Fact]
    public void PinnedGroup_RestoresPersistedCollapsedState()
    {
        SeedCollapsed("botnexus-pinned-collapsed", true);
        Seed(Conv("c-pin", "Pinned Chat", pinned: true));

        var cut = RenderLayout();

        cut.WaitForAssertion(() =>
            Assert.Empty(cut.Find("[data-testid='conversation-group-pinned']")
                .QuerySelectorAll("[data-testid='conversation-list-item']")));
    }

    /// <summary>Conversations gets a real &lt;button&gt; header toggle; activating it hides its items.</summary>
    [Fact]
    public void ConversationsGroup_ToggleIsAButton_AndCollapsesOnActivation()
    {
        Seed(Conv("c-normal", "Normal Chat"));

        var cut = RenderLayout();

        var toggle = cut.Find("[data-testid='conversations-group-toggle']");
        Assert.Equal("BUTTON", toggle.TagName);
        Assert.Contains("Normal Chat", cut.Find("[data-testid='conversation-group-conversations']").TextContent);

        cut.InvokeAsync(() => cut.Find("[data-testid='conversations-group-toggle']").Click());

        cut.WaitForAssertion(() =>
            Assert.DoesNotContain("Normal Chat", cut.Find("[data-testid='conversation-group-conversations']").TextContent));
    }

    /// <summary>Conversations collapse state is restored from the persisted localStorage key.</summary>
    [Fact]
    public void ConversationsGroup_RestoresPersistedCollapsedState()
    {
        SeedCollapsed("botnexus-conversations-collapsed", true);
        Seed(Conv("c-normal", "Normal Chat"));

        var cut = RenderLayout();

        cut.WaitForAssertion(() =>
            Assert.Empty(cut.Find("[data-testid='conversation-group-conversations']")
                .QuerySelectorAll("[data-testid='conversation-list-item']")));
    }

    // ------------------------------------------------------------------------ counts on headers

    /// <summary>
    /// Every group header renders its item count, and the counts are still in the DOM while the
    /// group is collapsed. Counts come from the same grouped lists the bodies render from.
    /// </summary>
    [Fact]
    public void GroupHeaders_ShowCounts_AndCountsSurviveCollapse()
    {
        SeedCollapsed("botnexus-pinned-collapsed", true);
        SeedCollapsed("botnexus-conversations-collapsed", true);

        Seed(
            Conv("c-pin-1", "Pin One", pinned: true),
            Conv("c-pin-2", "Pin Two", pinned: true),
            Conv("c-n-1", "Normal One"),
            Conv("c-n-2", "Normal Two"),
            Conv("c-n-3", "Normal Three"),
            Conv("c-cron", "Cron Run", source: "Cron"),
            Conv("c-hook-1", "Hook One", source: "Webhook"),
            Conv("c-hook-2", "Hook Two", source: "Webhook"));

        var cut = RenderLayout();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("2", cut.Find("[data-testid='pinned-group-count']").TextContent.Trim());
            Assert.Equal("3", cut.Find("[data-testid='conversations-group-count']").TextContent.Trim());
            Assert.Equal("1", cut.Find("[data-testid='cron-group-count']").TextContent.Trim());
            Assert.Equal("2", cut.Find("[data-testid='webhook-group-count']").TextContent.Trim());

            // Collapsed groups still show their counts - the whole point of the badge.
            Assert.Empty(cut.Find("[data-testid='conversation-group-pinned']")
                .QuerySelectorAll("[data-testid='conversation-list-item']"));
            Assert.Empty(cut.Find("[data-testid='conversation-group-conversations']")
                .QuerySelectorAll("[data-testid='conversation-list-item']"));
        });
    }
}

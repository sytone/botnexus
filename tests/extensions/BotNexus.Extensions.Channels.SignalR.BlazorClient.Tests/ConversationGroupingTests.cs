using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using System.Net;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ConversationGroupingTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store;
    private readonly IAgentInteractionService _interaction;
    private readonly IPortalLoadService _portalLoad;

    public ConversationGroupingTests()
    {
        _store = new ClientStateStore();
        _interaction = Substitute.For<IAgentInteractionService>();
        _portalLoad = Substitute.For<IPortalLoadService>();

        _portalLoad.IsReady.Returns(false);
        _portalLoad.IsLoading.Returns(true);
        _portalLoad.LoadError.Returns((string?)null);

        var hub = new GatewayHubConnection();
        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.ApiBaseUrl.Returns("");
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        var gatewayInfo = new GatewayInfoService(http, restClient);

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(hub);
        _ctx.Services.AddSingleton(gatewayInfo);
        _ctx.Services.AddSingleton(Substitute.For<IUpdateStatusService>());
        var mockPrefs = Substitute.For<IPortalPreferencesService>();
        mockPrefs.Current.Returns(new PortalPreferences());
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

    private IRenderedComponent<MainLayout> RenderLayout() =>
        _ctx.Render<MainLayout>(p => p
            .Add(c => c.Body, (RenderFragment)(_ => { })));

    private void SeedAgentWithConversations(params ConversationSummaryDto[] conversations)
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", conversations);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);
    }

    // Seeds the persisted "Scheduled" group collapse state to expanded ("false").
    // MainLayout.OnAfterRenderAsync reads localStorage["botnexus-cron-collapsed"]; under the Loose
    // JS mock an unseeded read returns null -> the group stays collapsed and only re-expands via a
    // toggle click. Because that read runs on an async continuation, it can re-collapse the group
    // after the click, producing a flaky "item missing from group body" failure under CI timing.
    // Seeding "false" makes the group render expanded deterministically with no toggle race.
    private void ExpandScheduledGroupByDefault() =>
        _ctx.JSInterop.Setup<string?>("localStorage.getItem", "botnexus-cron-collapsed").SetResult("false");

    [Fact]
    public void CronConversations_RenderedInScheduledGroup()
    {
        // Seed the Scheduled group as expanded so the rendered output is deterministic.
        // Without this, MainLayout.OnAfterRenderAsync reads localStorage (null under Loose mode
        // -> collapsed) on an async continuation that can re-collapse the group *after* a manual
        // toggle click, racing the assertion and intermittently failing in CI. Seeding the stored
        // value to "false" makes OnAfterRenderAsync land on expanded and removes the race entirely.
        ExpandScheduledGroupByDefault();

        // Arrange: one cronconv:-prefixed conversation
        SeedAgentWithConversations(
            new ConversationSummaryDto("cronconv:daily-check", "a-1", "Daily Check", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("c-1", "a-1", "Normal Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );

        var cut = RenderLayout();

        // The scheduled group should exist
        var scheduledGroup = cut.Find("[data-testid='conversation-group-scheduled']");
        Assert.NotNull(scheduledGroup);

        // WaitForAssertion lets the async OnAfterRenderAsync settle before we read the group body,
        // so the cron conversation is reliably rendered inside the expanded Scheduled group.
        cut.WaitForAssertion(() =>
            Assert.Contains("Daily Check", cut.Find("[data-testid='conversation-group-scheduled']").TextContent));
    }

    [Fact]
    public void CronConversations_VirtualKind_RenderedInScheduledGroup()
    {
        // Seed the Scheduled group as expanded so the rendered output is deterministic (see the
        // sibling test above for the OnAfterRenderAsync re-collapse race this avoids).
        ExpandScheduledGroupByDefault();

        // Arrange: conversation with IsVirtualSession + VirtualSessionKind = "cron"
        SeedAgentWithConversations(
            new ConversationSummaryDto("c-1", "a-1", "Cron Task", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("c-2", "a-1", "Normal Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );
        var cronConv = _store.GetAgent("a-1")!.Conversations["c-1"];
        cronConv.IsVirtualSession = true;
        cronConv.VirtualSessionKind = "cron";

        var cut = RenderLayout();

        var scheduledGroup = cut.Find("[data-testid='conversation-group-scheduled']");
        Assert.NotNull(scheduledGroup);

        // The virtual-kind cron conversation (IsVirtualSession + VirtualSessionKind == "cron")
        // should be grouped under Scheduled exactly like a cronconv:-prefixed one.
        cut.WaitForAssertion(() =>
            Assert.Contains("Cron Task", cut.Find("[data-testid='conversation-group-scheduled']").TextContent));
    }

    [Fact]
    public void ScheduledGroup_CollapsedByDefault()
    {
        // Arrange: a cron conversation
        SeedAgentWithConversations(
            new ConversationSummaryDto("cronconv:job-1", "a-1", "Cron Job", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("c-1", "a-1", "Normal Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );

        var cut = RenderLayout();

        // The scheduled group should exist
        var scheduledGroup = cut.Find("[data-testid='conversation-group-scheduled']");
        Assert.NotNull(scheduledGroup);

        // But the cron conversation items should NOT be visible (collapsed by default)
        var itemsInGroup = scheduledGroup.QuerySelectorAll("[data-testid='conversation-list-item']");
        Assert.Empty(itemsInGroup);
    }

    [Fact]
    public void ScheduledGroup_ShowsCount()
    {
        // Arrange: two cron conversations
        SeedAgentWithConversations(
            new ConversationSummaryDto("cronconv:job-1", "a-1", "Cron Job 1", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("cronconv:job-2", "a-1", "Cron Job 2", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("c-1", "a-1", "Normal Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );

        var cut = RenderLayout();

        // Find the count badge
        var countBadge = cut.Find(".conversation-group-count");
        Assert.Equal("2", countBadge.TextContent.Trim());
    }

    [Fact]
    public void PinnedGroup_OnlyShownWhenPinnedExist()
    {
        // Arrange: no pinned conversations
        SeedAgentWithConversations(
            new ConversationSummaryDto("c-1", "a-1", "Normal Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );

        var cut = RenderLayout();

        // Pinned group should NOT be rendered
        Assert.Empty(cut.FindAll("[data-testid='conversation-group-pinned']"));

        // Conversations group should still be present
        cut.Find("[data-testid='conversation-group-conversations']");
    }

    [Fact]
    public void PinnedConversations_RenderedInPinnedGroup()
    {
        // Arrange: one pinned conversation
        SeedAgentWithConversations(
            new ConversationSummaryDto("c-1", "a-1", "Important Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, IsPinned: true),
            new ConversationSummaryDto("c-2", "a-1", "Normal Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );

        var cut = RenderLayout();

        // Pinned group should be rendered
        var pinnedGroup = cut.Find("[data-testid='conversation-group-pinned']");
        Assert.Contains("Important Chat", pinnedGroup.TextContent);

        // Normal Chat should NOT be in the pinned group
        Assert.DoesNotContain("Normal Chat", pinnedGroup.TextContent);
    }

    [Fact]
    public void PinButton_Click_InvokesSetConversationPinned()
    {
        // Arrange: an unpinned normal conversation exposes a pin affordance.
        SeedAgentWithConversations(
            new ConversationSummaryDto("c-1", "a-1", "Pin Target", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );

        var cut = RenderLayout();

        // Wrap BOTH the Find and the Click inside a single InvokeAsync so no re-render can slip
        // between locating the button and dispatching its click — that gap is what invalidated the
        // event-handler id (bUnit UnknownEventHandlerIdException) and flaked this suite across PRs.
        cut.InvokeAsync(() => cut.Find("[data-testid='conversation-pin-btn']").Click());

        // Clicking pins the conversation via the interaction service (pinned: true).
        // InvokeAsync dispatches the click but returns a Task we don't await, so the async click
        // handler may not have completed when we verify the mock. WaitForAssertion retries the
        // Received(1) check until the async handler settles (or times out), eliminating the
        // received-call race that flaked this test under parallel CI load.
        cut.WaitForAssertion(() => _interaction.Received(1).SetConversationPinnedAsync("a-1", "c-1", true));
    }

    [Fact]
    public void PinButton_Click_OnPinnedConversation_Unpins()
    {
        // Arrange: an already-pinned conversation's pin button toggles it back off.
        SeedAgentWithConversations(
            new ConversationSummaryDto("c-1", "a-1", "Pinned Target", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, IsPinned: true)
        );

        var cut = RenderLayout();

        // Wrap BOTH the Find and the Click inside a single InvokeAsync so no re-render can slip
        // between locating the button and dispatching its click — the captured handler id would
        // otherwise go stale after an optimistic re-render (bUnit UnknownEventHandlerIdException),
        // which surfaced the stale-handler exception across multiple PRs under parallel CI load.
        cut.InvokeAsync(() => cut.Find("[data-testid='conversation-pin-btn']").Click());

        // InvokeAsync dispatches the click but returns a Task we don't await, so the async click
        // handler may not have completed when we verify the mock. WaitForAssertion retries the
        // Received(1) check until the async handler settles (or times out), eliminating the
        // received-call race that flaked this test under parallel CI load.
        cut.WaitForAssertion(() => _interaction.Received(1).SetConversationPinnedAsync("a-1", "c-1", false));
    }

    [Fact]
    public void NormalConversations_RenderedInConversationsGroup()
    {
        // Arrange: mix of pinned, normal, and cron conversations
        SeedAgentWithConversations(
            new ConversationSummaryDto("c-1", "a-1", "Pinned Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, IsPinned: true),
            new ConversationSummaryDto("c-2", "a-1", "Normal Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("cronconv:job-1", "a-1", "Cron Job", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );

        var cut = RenderLayout();

        // Normal conversations group should have "Normal Chat"
        var convsGroup = cut.Find("[data-testid='conversation-group-conversations']");
        Assert.Contains("Normal Chat", convsGroup.TextContent);

        // Pinned should NOT be in the normal conversations group
        Assert.DoesNotContain("Pinned Chat", convsGroup.TextContent);

        // Cron should NOT be in the normal conversations group
        Assert.DoesNotContain("Cron Job", convsGroup.TextContent);
    }

    [Fact]
    public void ConversationAssignedToCronJob_RenderedInScheduledGroup()
    {
        // Arrange: set up a mock HTTP handler that returns cron jobs with a conversationId
        var handler = new MockCronHttpHandler();
        handler.SetCronResponse("[{\"id\":\"job-1\",\"name\":\"Daily Digest\",\"schedule\":\"0 8 * * *\",\"enabled\":true,\"conversationId\":\"conv:assigned-to-cron\"}]");
        using var ctx = new BunitContext();
        var httpWithMock = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.ApiBaseUrl.Returns("");
        var store = new ClientStateStore();
        ctx.Services.AddSingleton<IClientStateStore>(store);
        ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(false);
        portalLoad.IsLoading.Returns(true);
        portalLoad.LoadError.Returns((string?)null);
        ctx.Services.AddSingleton(portalLoad);
        ctx.Services.AddSingleton(new GatewayHubConnection());
        ctx.Services.AddSingleton(new GatewayInfoService(httpWithMock, restClient));
        ctx.Services.AddSingleton(Substitute.For<IUpdateStatusService>());
        var mockPrefs = Substitute.For<IPortalPreferencesService>();
        mockPrefs.Current.Returns(new PortalPreferences());
        ctx.Services.AddSingleton(mockPrefs);
        ctx.Services.AddSingleton(restClient);
        ctx.Services.AddSingleton(Substitute.For<IChannelErrorReporter>());
        ctx.Services.AddSingleton(httpWithMock);
        ctx.Services.AddSingleton(new ExtensionFeatureService(restClient));
        ctx.Services.AddSingleton(new CronApiClient(httpWithMock));
        ctx.Services.AddSingleton(new SectionsApiClient(httpWithMock));
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        // Render the Scheduled group expanded by default so the assertion does not depend on a
        // toggle click racing the async OnAfterRenderAsync localStorage read (see the
        // ExpandScheduledGroupByDefault helper for the shared-context tests).
        ctx.JSInterop.Setup<string?>("localStorage.getItem", "botnexus-cron-collapsed").SetResult("false");

        store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        store.SeedConversations("a-1", [
            new ConversationSummaryDto("conv:assigned-to-cron", "a-1", "Digest Conv", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("conv:normal", "a-1", "Normal Conv", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = ctx.Render<MainLayout>(p => p
            .Add(c => c.Body, (RenderFragment)(_ => { })));

        // The cron->conversation id mapping is fetched asynchronously in OnAfterRenderAsync
        // (LoadCronConversationIdsAsync). WaitForAssertion retries until that async load and the
        // resulting re-render have settled, so the assigned conversation reliably appears in the
        // expanded Scheduled group without a fixed Task.Delay or a toggle click.
        cut.WaitForAssertion(() =>
            Assert.Contains("Digest Conv", cut.Find("[data-testid='conversation-group-scheduled']").TextContent));

        // The normal conversation should NOT be in the scheduled group
        Assert.DoesNotContain("Normal Conv", cut.Find("[data-testid='conversation-group-scheduled']").TextContent);
    }

    private sealed class MockCronHttpHandler : HttpMessageHandler
    {
        private string _cronJson = "[]";

        public void SetCronResponse(string json) => _cronJson = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.Contains("/api/cron", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(_cronJson, System.Text.Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
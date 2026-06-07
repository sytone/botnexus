using Bunit;
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
        _store.ActiveAgentId = "a-1";
    }

    [Fact]
    public void CronConversations_RenderedInScheduledGroup()
    {
        // Arrange: one cronconv:-prefixed conversation
        SeedAgentWithConversations(
            new ConversationSummaryDto("cronconv:daily-check", "a-1", "Daily Check", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("c-1", "a-1", "Normal Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );

        var cut = RenderLayout();

        // The scheduled group should exist
        var scheduledGroup = cut.Find("[data-testid='conversation-group-scheduled']");
        Assert.NotNull(scheduledGroup);

        // The cron conversation title should be inside the scheduled group (when expanded)
        // First expand the group
        cut.Find("[data-testid='cron-group-toggle']").Click();
        scheduledGroup = cut.Find("[data-testid='conversation-group-scheduled']");
        Assert.Contains("Daily Check", scheduledGroup.TextContent);
    }

    [Fact]
    public void CronConversations_VirtualKind_RenderedInScheduledGroup()
    {
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

        // Expand and verify
        cut.Find("[data-testid='cron-group-toggle']").Click();
        scheduledGroup = cut.Find("[data-testid='conversation-group-scheduled']");
        Assert.Contains("Cron Task", scheduledGroup.TextContent);
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
}

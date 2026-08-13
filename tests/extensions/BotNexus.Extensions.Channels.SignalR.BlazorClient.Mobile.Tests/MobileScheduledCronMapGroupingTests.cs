using System.Net;
using System.Text;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for issue #3073: the mobile picker classified scheduled conversations with only the FIRST
/// of the desktop sidebar's two clauses, so a conversation whose <c>Source</c> is
/// <see cref="ConversationSource.Channel"/> but whose id is the target of a cron job rendered under
/// the generic <c>Conversations</c> optgroup instead of <c>Scheduled</c> (61 conversations on the
/// reporting instance).
/// </summary>
/// <remarks>
/// <para>
/// The origin signal is write-once (#2304 / epic #2300), so a conversation adopted by a cron job
/// after creation keeps <c>Source = Channel</c> forever. The authoritative cron-job to
/// conversation-id map returned by <c>GET /api/cron</c> is the ONLY signal that identifies those
/// rows, which is exactly why <c>MainLayout.IsCronConversation</c> has always consulted it.
/// </para>
/// <para>
/// Mutation anchor for AC5: reverting
/// <see cref="PortalConversationGrouping.IsScheduled(ConversationState, SelectionSource, IReadOnlySet{string}?)"/>
/// to the projection-only rule must redden
/// <see cref="Cron_mapped_channel_sourced_conversation_is_grouped_under_Scheduled"/> and
/// <see cref="Mobile_picker_groups_a_cron_mapped_channel_conversation_under_Scheduled"/> by name.
/// </para>
/// </remarks>
public sealed class MobileScheduledCronMapGroupingTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    // ---- Shared classifier contract (AC1 / AC4) ----

    [Fact]
    public void Cron_mapped_channel_sourced_conversation_is_grouped_under_Scheduled()
    {
        var groups = PortalConversationGrouping.ForPicker(
            [Conv("adopted"), Conv("plain")],
            SelectionSource.UserClick,
            CronIds("adopted"));

        var scheduled = Assert.Single(groups, g => g.Label == PortalConversationGrouping.ScheduledLabel);
        Assert.Equal(["adopted"], scheduled.Conversations.Select(c => c.ConversationId));

        // Sad path: the conversation that is NOT cron-backed must stay in the generic group.
        var main = Assert.Single(groups, g => g.Label == PortalConversationGrouping.ConversationsLabel);
        Assert.Equal(["plain"], main.Conversations.Select(c => c.ConversationId));
    }

    [Fact]
    public void Cron_mapped_ids_are_matched_case_insensitively_like_the_desktop_set()
    {
        var groups = PortalConversationGrouping.ForPicker(
            [Conv("Adopted")],
            SelectionSource.UserClick,
            CronIds("adopted"));

        var scheduled = Assert.Single(groups);
        Assert.Equal(PortalConversationGrouping.ScheduledLabel, scheduled.Label);
    }

    [Fact]
    public void A_conversation_never_appears_in_two_groups_when_the_cron_map_is_supplied()
    {
        var groups = PortalConversationGrouping.ForPicker(
            [Conv("adopted"), Conv("cron", source: ConversationSource.Cron), Conv("plain")],
            SelectionSource.UserClick,
            CronIds("adopted", "cron", "plain-not-present"));

        var ids = groups.SelectMany(g => g.Conversations.Select(c => c.ConversationId)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(["adopted", "cron", "plain"], ids.Order());
    }

    [Fact]
    public void Pinning_still_wins_over_a_cron_mapped_conversation()
    {
        var groups = PortalConversationGrouping.ForPicker(
            [Conv("adopted", isPinned: true)],
            SelectionSource.UserClick,
            CronIds("adopted"));

        var group = Assert.Single(groups);
        Assert.Equal(PortalConversationGrouping.PinnedLabel, group.Label);
    }

    [Fact]
    public void An_absent_cron_map_degrades_to_projection_only_grouping()
    {
        // AC4: a failed/empty /api/cron fetch must never throw nor empty the picker - it degrades
        // to exactly the pre-#3073 behaviour.
        foreach (IReadOnlySet<string>? map in new IReadOnlySet<string>?[] { null, new HashSet<string>() })
        {
            var groups = PortalConversationGrouping.ForPicker(
                [Conv("adopted"), Conv("cron", source: ConversationSource.Cron)],
                SelectionSource.UserClick,
                map);

            var scheduled = Assert.Single(groups, g => g.Label == PortalConversationGrouping.ScheduledLabel);
            Assert.Equal(["cron"], scheduled.Conversations.Select(c => c.ConversationId));
            var main = Assert.Single(groups, g => g.Label == PortalConversationGrouping.ConversationsLabel);
            Assert.Equal(["adopted"], main.Conversations.Select(c => c.ConversationId));
        }
    }

    [Fact]
    public void IsScheduled_is_the_single_predicate_and_answers_both_clauses()
    {
        var map = CronIds("adopted");

        Assert.True(PortalConversationGrouping.IsScheduled(
            Conv("cron", source: ConversationSource.Cron), SelectionSource.UserClick, map));
        Assert.True(PortalConversationGrouping.IsScheduled(
            Conv("adopted"), SelectionSource.UserClick, map));
        Assert.False(PortalConversationGrouping.IsScheduled(
            Conv("plain"), SelectionSource.UserClick, map));
        Assert.False(PortalConversationGrouping.IsScheduled(
            Conv("plain"), SelectionSource.UserClick, cronConversationIds: null));
    }

    [Fact]
    public void CronConversationIds_projects_the_job_list_the_desktop_way()
    {
        var set = PortalConversationGrouping.CronConversationIds(
        [
            new CronJobDto { Id = "a", ConversationId = "conv-a" },
            new CronJobDto { Id = "b", ConversationId = null },
            new CronJobDto { Id = "c", ConversationId = "" },
            new CronJobDto { Id = "d", ConversationId = "conv-a" },
        ]);

        Assert.Equal(["conv-a"], set.Order());
        Assert.Contains("CONV-A", set);
    }

    [Fact]
    public void CronConversationIds_of_an_empty_or_null_job_list_is_an_empty_set()
    {
        Assert.Empty(PortalConversationGrouping.CronConversationIds([]));
        Assert.Empty(PortalConversationGrouping.CronConversationIds(null));
    }

    // ---- Mobile rendering (AC1 happy path + sad path) ----

    [Fact]
    public void Mobile_picker_groups_a_cron_mapped_channel_conversation_under_Scheduled()
    {
        var agent = Agent();
        Add(agent, Conv("adopted", title: "Morning Check-in"));
        Add(agent, Conv("plain", title: "Ad-hoc"));
        BuildStore([agent], "keel", cronJson: """[{"id":"morning","conversationId":"adopted"}]""");

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "keel"));

        cut.WaitForAssertion(() =>
        {
            var select = cut.Find("select.conv-select");
            var scheduled = Assert.Single(
                select.QuerySelectorAll("optgroup"),
                g => g.GetAttribute("label") == PortalConversationGrouping.ScheduledLabel);
            Assert.Equal(
                ["Morning Check-in"],
                scheduled.QuerySelectorAll("option").Select(o => o.TextContent.Trim()));

            // Sad path: the non-cron conversation must NOT be dragged into Scheduled.
            var main = Assert.Single(
                select.QuerySelectorAll("optgroup"),
                g => g.GetAttribute("label") == PortalConversationGrouping.ConversationsLabel);
            Assert.Equal(["Ad-hoc"], main.QuerySelectorAll("option").Select(o => o.TextContent.Trim()));
        });
    }

    [Fact]
    public void Mobile_picker_still_renders_every_conversation_when_the_cron_fetch_fails()
    {
        // AC4 at the render seam: membership is unchanged and nothing throws.
        var agent = Agent();
        Add(agent, Conv("adopted", title: "Morning Check-in"));
        Add(agent, Conv("plain", title: "Ad-hoc"));
        BuildStore([agent], "keel", cronStatus: HttpStatusCode.InternalServerError);

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "keel"));

        var options = cut.Find("select.conv-select").QuerySelectorAll("option")
            .Select(o => o.TextContent.Trim()).Order().ToList();
        Assert.Equal(["Ad-hoc", "Morning Check-in"], options);
        var group = Assert.Single(cut.Find("select.conv-select").QuerySelectorAll("optgroup"));
        Assert.Equal(PortalConversationGrouping.ConversationsLabel, group.GetAttribute("label"));
    }

    // ---- Helpers ----

    private static IReadOnlySet<string> CronIds(params string[] ids)
        => ids.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static AgentState Agent() => new()
    {
        AgentId = "keel",
        DisplayName = "Keel",
        IsConnected = true,
        ActiveConversationId = "plain"
    };

    private static void Add(AgentState agent, ConversationState conv)
        => agent.Conversations[conv.ConversationId] = conv;

    private static ConversationState Conv(
        string id,
        bool isPinned = false,
        ConversationSource source = ConversationSource.Channel,
        string? title = null)
        => new()
        {
            ConversationId = id,
            IsPinned = isPinned,
            Source = source,
            UpdatedAt = DateTimeOffset.UtcNow,
            Status = "Active",
            Title = title ?? id
        };

    private void BuildStore(
        IReadOnlyList<AgentState> agents,
        string activeAgentId,
        string cronJson = "[]",
        HttpStatusCode cronStatus = HttpStatusCode.OK)
    {
        var store = Substitute.For<IClientStateStore>();
        var portalLoad = Substitute.For<IPortalLoadService>();
        var interaction = Substitute.For<IAgentInteractionService>();

        portalLoad.IsReady.Returns(true);
        portalLoad.IsLoading.Returns(false);
        portalLoad.LoadError.Returns((string?)null);
        portalLoad.IsSignalRConnected.Returns(true);
        portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var dict = agents.ToDictionary(a => a.AgentId, a => a);
        store.Agents.Returns(dict.AsReadOnly());
        store.ActiveAgentId.Returns(activeAgentId);
        foreach (var a in agents)
        {
            store.GetAgent(a.AgentId).Returns(a);
        }

        store.GetStreamState(Arg.Any<string>()).Returns(new ConversationStreamState());
        store.GetMessages(Arg.Any<string>()).Returns(new List<ChatMessage>());

        _ctx.Services.AddSingleton(store);
        _ctx.Services.AddSingleton(portalLoad);
        _ctx.Services.AddSingleton(new BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services.MobileHubTuningOptions());
        _ctx.Services.AddSingleton(interaction);
        _ctx.Services.AddSingleton(new CronApiClient(new HttpClient(new StubCronHandler(cronJson, cronStatus))
        {
            BaseAddress = new Uri("http://localhost/")
        }));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private sealed class StubCronHandler(string json, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}

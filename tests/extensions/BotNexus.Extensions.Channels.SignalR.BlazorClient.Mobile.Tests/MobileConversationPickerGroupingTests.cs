using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for issue #2327: the mobile conversation picker rendered a single flat option list while
/// the desktop sidebar already grouped the same conversations into Pinned / Conversations /
/// Scheduled. Mobile now renders the identical partition as native <c>&lt;optgroup&gt;</c> elements,
/// derived from the shared <see cref="PortalConversationGrouping"/> helper rather than a
/// mobile-local reimplementation of the grouping rules.
/// </summary>
/// <remarks>
/// Grouping inputs are the same immutable signals the desktop sidebar uses:
/// <see cref="ConversationState.IsPinned"/> and the <see cref="ConversationRenderProjection.Group"/>
/// projection over the server-supplied <c>(Kind, Source)</c> origin pair. No new injected service is
/// introduced, so no other bUnit context that renders <see cref="Chat"/> is affected.
/// </remarks>
public sealed class MobileConversationPickerGroupingTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    // ---- PortalConversationGrouping contract (shared helper, form-factor agnostic) ----

    [Fact]
    public void Grouping_puts_pinned_group_first()
    {
        var groups = PortalConversationGrouping.ForPicker(
            [Conv("normal"), Conv("pin", isPinned: true)],
            SelectionSource.UserClick);

        Assert.Equal(PortalConversationGrouping.PinnedLabel, groups[0].Label);
        Assert.Equal(["pin"], groups[0].Conversations.Select(c => c.ConversationId));
    }

    [Fact]
    public void Grouping_places_scheduled_conversations_in_their_own_group()
    {
        var groups = PortalConversationGrouping.ForPicker(
            [Conv("normal"), Conv("cron", source: ConversationSource.Cron)],
            SelectionSource.UserClick);

        var scheduled = Assert.Single(groups, g => g.Label == PortalConversationGrouping.ScheduledLabel);
        Assert.Equal(["cron"], scheduled.Conversations.Select(c => c.ConversationId));

        // ...and it is NOT mixed into the main conversations group.
        var main = Assert.Single(groups, g => g.Label == PortalConversationGrouping.ConversationsLabel);
        Assert.DoesNotContain(main.Conversations, c => c.ConversationId == "cron");
    }

    [Fact]
    public void Grouping_orders_groups_pinned_then_conversations_then_scheduled()
    {
        var groups = PortalConversationGrouping.ForPicker(
            [Conv("cron", source: ConversationSource.Cron), Conv("normal"), Conv("pin", isPinned: true)],
            SelectionSource.UserClick);

        Assert.Equal(
            [
                PortalConversationGrouping.PinnedLabel,
                PortalConversationGrouping.ConversationsLabel,
                PortalConversationGrouping.ScheduledLabel
            ],
            groups.Select(g => g.Label));
    }

    [Fact]
    public void Grouping_pin_wins_over_scheduled_matching_desktop_precedence()
    {
        // Desktop's sidebar filters pinned first and excludes those ids from the cron group, so a
        // pinned cron conversation appears exactly once, under Pinned.
        var groups = PortalConversationGrouping.ForPicker(
            [Conv("pinned-cron", isPinned: true, source: ConversationSource.Cron)],
            SelectionSource.UserClick);

        var group = Assert.Single(groups);
        Assert.Equal(PortalConversationGrouping.PinnedLabel, group.Label);
    }

    [Fact]
    public void Grouping_omits_empty_groups_entirely()
    {
        var groups = PortalConversationGrouping.ForPicker([Conv("only")], SelectionSource.UserClick);

        var group = Assert.Single(groups);
        Assert.Equal(PortalConversationGrouping.ConversationsLabel, group.Label);
    }

    [Fact]
    public void Grouping_returns_no_groups_for_an_empty_conversation_set()
        => Assert.Empty(PortalConversationGrouping.ForPicker([], SelectionSource.UserClick));

    [Fact]
    public void Grouping_orders_within_a_group_using_the_shared_display_ordering()
    {
        var now = DateTimeOffset.UtcNow;
        var groups = PortalConversationGrouping.ForPicker(
            [
                Conv("old", updated: now.AddHours(-3)),
                Conv("newest", updated: now),
                Conv("default", isDefault: true, updated: now.AddDays(-9))
            ],
            SelectionSource.UserClick);

        var main = Assert.Single(groups);
        Assert.Equal(["default", "newest", "old"], main.Conversations.Select(c => c.ConversationId));
    }

    // ---- Mobile rendering (happy path) ----

    [Fact]
    public void Mobile_picker_renders_pinned_group_first_then_conversations_then_scheduled()
    {
        var agent = Agent();
        Add(agent, Conv("normal", title: "Normal"));
        Add(agent, Conv("pin", isPinned: true, title: "Pinned One"));
        Add(agent, Conv("cron", source: ConversationSource.Cron, title: "Nightly"));
        BuildStore([agent], "quill");

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "quill"));

        var labels = cut.Find("select.conv-select").QuerySelectorAll("optgroup")
            .Select(g => g.GetAttribute("label")).ToList();
        Assert.Equal(
            [
                PortalConversationGrouping.PinnedLabel,
                PortalConversationGrouping.ConversationsLabel,
                PortalConversationGrouping.ScheduledLabel
            ],
            labels);

        // Pinned option is the first option in the whole picker.
        var options = cut.Find("select.conv-select").QuerySelectorAll("option")
            .Select(o => o.TextContent.Trim()).ToList();
        Assert.Equal(["Pinned One", "Normal", "Nightly"], options);
    }

    [Fact]
    public void Mobile_picker_still_renders_ungrouped_conversations()
    {
        var agent = Agent();
        Add(agent, Conv("a", title: "Alpha"));
        Add(agent, Conv("b", title: "Beta"));
        BuildStore([agent], "quill");

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "quill"));

        var select = cut.Find("select.conv-select");
        var group = Assert.Single(select.QuerySelectorAll("optgroup"));
        Assert.Equal(PortalConversationGrouping.ConversationsLabel, group.GetAttribute("label"));
        Assert.Equal(2, select.QuerySelectorAll("option").Length);
    }

    // ---- Mobile rendering (sad paths) ----

    [Fact]
    public void Mobile_picker_does_not_render_an_empty_optgroup()
    {
        var agent = Agent();
        Add(agent, Conv("cron", source: ConversationSource.Cron, title: "Nightly"));
        BuildStore([agent], "quill");

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "quill"));

        var select = cut.Find("select.conv-select");
        var group = Assert.Single(select.QuerySelectorAll("optgroup"));
        Assert.Equal(PortalConversationGrouping.ScheduledLabel, group.GetAttribute("label"));
        Assert.DoesNotContain(
            select.QuerySelectorAll("optgroup"),
            g => g.QuerySelectorAll("option").Length == 0);
    }

    [Fact]
    public void Mobile_picker_renders_no_optgroups_when_the_agent_has_no_active_conversations()
    {
        var agent = Agent();
        Add(agent, Conv("archived", status: "Archived", title: "Gone"));
        BuildStore([agent], "quill");

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "quill"));

        var select = cut.Find("select.conv-select");
        Assert.Empty(select.QuerySelectorAll("optgroup"));
        Assert.Empty(select.QuerySelectorAll("option"));
    }

    [Fact]
    public void Mobile_picker_selection_still_switches_conversation_from_inside_a_group()
    {
        var agent = Agent();
        Add(agent, Conv("normal", title: "Normal"));
        Add(agent, Conv("cron", source: ConversationSource.Cron, title: "Nightly"));
        var interaction = BuildStore([agent], "quill");

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "quill"));
        cut.Find("select.conv-select").Change("cron");

        // Selection routes through the same call the flat picker used.
        interaction.Received().SelectConversationAsync("quill", "cron");
    }

    // ---- Helpers ----

    private static AgentState Agent() => new()
    {
        AgentId = "quill",
        DisplayName = "Quill",
        IsConnected = true,
        ActiveConversationId = "normal"
    };

    private static void Add(AgentState agent, ConversationState conv)
        => agent.Conversations[conv.ConversationId] = conv;

    private static ConversationState Conv(
        string id,
        bool isPinned = false,
        bool isDefault = false,
        ConversationSource source = ConversationSource.Channel,
        DateTimeOffset? updated = null,
        string? title = null,
        string status = "Active")
        => new()
        {
            ConversationId = id,
            IsPinned = isPinned,
            IsDefault = isDefault,
            Source = source,
            UpdatedAt = updated ?? DateTimeOffset.UtcNow,
            Status = status,
            Title = title ?? id
        };

    private IAgentInteractionService BuildStore(IReadOnlyList<AgentState> agents, string activeAgentId)
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
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return interaction;
    }
}

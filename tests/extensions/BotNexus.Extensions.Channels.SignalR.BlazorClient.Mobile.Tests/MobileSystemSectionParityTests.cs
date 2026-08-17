using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Pins the #2709 decision: mobile navigation adopts all FOUR desktop system-section memberships
/// (Pinned / Conversations / Scheduled / Webhooks) while deliberately omitting the desktop collapse
/// presentation and its <c>botnexus-*-collapsed</c> localStorage keys.
/// </summary>
/// <remarks>
/// <para>
/// <b>What actually diverged.</b> Mobile has no sidebar - <c>MobileLayout.razor</c> is an error
/// boundary plus a reconnect overlay, and conversation navigation is a native <c>&lt;select&gt;</c>
/// in <c>Chat.razor</c> fed by <see cref="PortalConversationGrouping.ForPicker"/>. That helper
/// emitted three groups, so webhook-originated runs - the unattended machine traffic #2122 split out
/// on desktop precisely because it swamps the list - fell into the user's own <c>Conversations</c>
/// group. The mobile list is the narrower surface, so it swamps sooner.
/// </para>
/// <para>
/// <b>What is deliberately omitted, and why it needs a test (AC6).</b> A native
/// <c>&lt;optgroup&gt;</c> is rendered by the OS and has no disclosure affordance, so there is no
/// collapsed state to hold and nothing for a storage key to persist. Adding mobile collapse keys
/// would write dead state that later drifts against desktop's live state. The
/// <c>Mobile_navigation_persists_no_section_collapse_state</c> and
/// <c>Mobile_navigation_renders_no_section_collapse_affordance</c> cases pin that omission so it
/// cannot silently regress into a half-built second mechanism.
/// </para>
/// <para>
/// <b>Membership provenance (AC2).</b> Every group here derives from the one typed projection,
/// <see cref="ConversationRenderProjection.Group"/> over the immutable server-supplied
/// <c>(Kind, Source)</c> pair - plus, for Scheduled only, the authoritative cron-id map that
/// <c>Source</c> being write-once (#2304) makes unavoidable. No title or id-prefix heuristic exists
/// on this path, and <c>MobileSectionMembership_UsesProvenanceNotTitleOrIdHeuristics</c> asserts
/// that directly by grouping conversations whose titles and ids actively lie about their origin.
/// </para>
/// </remarks>
public sealed class MobileSystemSectionParityTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    // ── AC1/AC4: the four memberships and their precedence, in the shared helper ──────────────

    [Fact]
    public void Grouping_gives_webhook_runs_their_own_group()
    {
        var groups = PortalConversationGrouping.ForPicker(
            [Conv("normal"), Conv("hook", source: ConversationSource.Webhook)],
            SelectionSource.UserClick);

        var webhooks = Assert.Single(groups, g => g.Label == PortalConversationGrouping.WebhooksLabel);
        Assert.Equal(["hook"], webhooks.Conversations.Select(c => c.ConversationId));

        // ...and it is no longer mixed into the user's own conversations, which was the defect.
        var main = Assert.Single(groups, g => g.Label == PortalConversationGrouping.ConversationsLabel);
        Assert.DoesNotContain(main.Conversations, c => c.ConversationId == "hook");
    }

    [Fact]
    public void Grouping_orders_the_four_sections_exactly_as_desktop_does()
    {
        var groups = PortalConversationGrouping.ForPicker(
            [
                Conv("hook", source: ConversationSource.Webhook),
                Conv("cron", source: ConversationSource.Cron),
                Conv("normal"),
                Conv("pin", isPinned: true)
            ],
            SelectionSource.UserClick);

        Assert.Equal(
            [
                PortalConversationGrouping.PinnedLabel,
                PortalConversationGrouping.ConversationsLabel,
                PortalConversationGrouping.ScheduledLabel,
                PortalConversationGrouping.WebhooksLabel
            ],
            groups.Select(g => g.Label));
    }

    [Fact]
    public void Grouping_pin_wins_over_webhook_matching_desktop_precedence()
    {
        // Desktop: !IsPinned && !IsCron && IsWebhook - pinned is subtracted first, so a pinned
        // webhook run appears exactly once, at the top.
        var groups = PortalConversationGrouping.ForPicker(
            [Conv("pinned-hook", isPinned: true, source: ConversationSource.Webhook)],
            SelectionSource.UserClick);

        var group = Assert.Single(groups);
        Assert.Equal(PortalConversationGrouping.PinnedLabel, group.Label);
    }

    [Fact]
    public void Grouping_scheduled_wins_over_webhook_matching_desktop_precedence()
    {
        // A conversation that a cron job has adopted is Scheduled even though its own immutable
        // Source says Webhook - desktop subtracts cron before testing the webhook predicate.
        var cronIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "adopted" };

        var groups = PortalConversationGrouping.ForPicker(
            [Conv("adopted", source: ConversationSource.Webhook)],
            SelectionSource.UserClick,
            cronIds);

        var group = Assert.Single(groups);
        Assert.Equal(PortalConversationGrouping.ScheduledLabel, group.Label);
    }

    [Fact]
    public void Grouping_places_every_conversation_in_exactly_one_section()
    {
        var all = new[]
        {
            Conv("pin", isPinned: true),
            Conv("normal"),
            Conv("cron", source: ConversationSource.Cron),
            Conv("hook", source: ConversationSource.Webhook),
            Conv("pinned-hook", isPinned: true, source: ConversationSource.Webhook)
        };

        var groups = PortalConversationGrouping.ForPicker(all, SelectionSource.UserClick);

        var placed = groups.SelectMany(g => g.Conversations).Select(c => c.ConversationId).ToList();
        Assert.Equal(all.Length, placed.Count);
        Assert.Equal(placed.Count, placed.Distinct().Count());
        Assert.Equal(all.Select(c => c.ConversationId).Order(), placed.Order());
    }

    [Fact]
    public void Grouping_omits_the_webhooks_section_when_no_webhook_runs_exist()
    {
        var groups = PortalConversationGrouping.ForPicker([Conv("normal")], SelectionSource.UserClick);

        Assert.DoesNotContain(groups, g => g.Label == PortalConversationGrouping.WebhooksLabel);
    }

    // ── AC2: provenance, not heuristics ───────────────────────────────────────────────────────

    [Fact]
    public void MobileSectionMembership_UsesProvenanceNotTitleOrIdHeuristics()
    {
        // Titles and ids deliberately lie about origin in BOTH directions. A title/prefix-sniffing
        // classifier would group these backwards; the typed projection ignores both fields.
        var groups = PortalConversationGrouping.ForPicker(
            [
                Conv("webhook-looking-id", title: "Webhook nightly run"),
                Conv("c_plain", title: "Just chatting", source: ConversationSource.Webhook),
                Conv("cron-looking-id", title: "Cron sweep")
            ],
            SelectionSource.UserClick);

        var webhooks = Assert.Single(groups, g => g.Label == PortalConversationGrouping.WebhooksLabel);
        Assert.Equal(["c_plain"], webhooks.Conversations.Select(c => c.ConversationId));

        var main = Assert.Single(groups, g => g.Label == PortalConversationGrouping.ConversationsLabel);
        Assert.Equal(
            ["cron-looking-id", "webhook-looking-id"],
            main.Conversations.Select(c => c.ConversationId).Order());
    }

    // ── AC5: mobile rendering, happy path ─────────────────────────────────────────────────────

    [Fact]
    public void Mobile_picker_renders_all_four_sections_in_desktop_order()
    {
        var agent = Agent();
        Add(agent, Conv("normal", title: "Normal"));
        Add(agent, Conv("pin", isPinned: true, title: "Pinned One"));
        Add(agent, Conv("cron", source: ConversationSource.Cron, title: "Nightly"));
        Add(agent, Conv("hook", source: ConversationSource.Webhook, title: "Inbound Hook"));
        BuildStore([agent], "quill");

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "quill"));

        var select = cut.Find("select.conv-select");
        Assert.Equal(
            [
                PortalConversationGrouping.PinnedLabel,
                PortalConversationGrouping.ConversationsLabel,
                PortalConversationGrouping.ScheduledLabel,
                PortalConversationGrouping.WebhooksLabel
            ],
            select.QuerySelectorAll("optgroup").Select(g => g.GetAttribute("label")));

        Assert.Equal(
            ["Pinned One", "Normal", "Nightly", "Inbound Hook"],
            select.QuerySelectorAll("option").Select(o => o.TextContent.Trim()));
    }

    [Fact]
    public void Mobile_picker_keeps_webhook_runs_out_of_the_user_conversations_group()
    {
        var agent = Agent();
        Add(agent, Conv("normal", title: "Normal"));
        Add(agent, Conv("hook", source: ConversationSource.Webhook, title: "Inbound Hook"));
        BuildStore([agent], "quill");

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "quill"));

        var main = Assert.Single(
            cut.Find("select.conv-select").QuerySelectorAll("optgroup"),
            g => g.GetAttribute("label") == PortalConversationGrouping.ConversationsLabel);
        Assert.Equal(["Normal"], main.QuerySelectorAll("option").Select(o => o.TextContent.Trim()));
    }

    [Fact]
    public void Mobile_picker_selection_switches_to_a_conversation_inside_the_webhooks_section()
    {
        var agent = Agent();
        Add(agent, Conv("normal", title: "Normal"));
        Add(agent, Conv("hook", source: ConversationSource.Webhook, title: "Inbound Hook"));
        var interaction = BuildStore([agent], "quill");

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "quill"));
        cut.Find("select.conv-select").Change("hook");

        interaction.Received().SelectConversationAsync("quill", "hook");
    }

    // ── AC5: mobile rendering, empty-section / sad paths ──────────────────────────────────────

    [Fact]
    public void Mobile_picker_renders_no_webhooks_optgroup_when_the_section_is_empty()
    {
        var agent = Agent();
        Add(agent, Conv("normal", title: "Normal"));
        BuildStore([agent], "quill");

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "quill"));

        var labels = cut.Find("select.conv-select").QuerySelectorAll("optgroup")
            .Select(g => g.GetAttribute("label")).ToList();
        Assert.DoesNotContain(PortalConversationGrouping.WebhooksLabel, labels);
        Assert.Single(labels);
    }

    [Fact]
    public void Mobile_picker_renders_a_lone_webhooks_section_with_no_empty_siblings()
    {
        var agent = Agent();
        Add(agent, Conv("hook", source: ConversationSource.Webhook, title: "Inbound Hook"));
        BuildStore([agent], "quill");

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "quill"));

        var select = cut.Find("select.conv-select");
        var group = Assert.Single(select.QuerySelectorAll("optgroup"));
        Assert.Equal(PortalConversationGrouping.WebhooksLabel, group.GetAttribute("label"));
        Assert.DoesNotContain(
            select.QuerySelectorAll("optgroup"),
            g => g.QuerySelectorAll("option").Length == 0);
    }

    [Fact]
    public void Mobile_picker_renders_nothing_when_all_conversations_are_archived()
    {
        var agent = Agent();
        Add(agent, Conv("archived-hook", source: ConversationSource.Webhook, status: "Archived"));
        BuildStore([agent], "quill");

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "quill"));

        var select = cut.Find("select.conv-select");
        Assert.Empty(select.QuerySelectorAll("optgroup"));
        Assert.Empty(select.QuerySelectorAll("option"));
    }

    // ── AC3/AC6: the deliberate divergence, pinned so it cannot silently regress ───────────────

    [Fact]
    public void Mobile_navigation_renders_no_section_collapse_affordance()
    {
        // The decision is that mobile adopts section MEMBERSHIP and omits the desktop collapse
        // PRESENTATION, because a native <optgroup> is rendered by the OS and cannot host a
        // disclosure control. If a bottom-sheet nav ever replaces the picker, this test fails and
        // forces clause 3 of #2709 to be re-decided rather than drifted into.
        var agent = Agent();
        Add(agent, Conv("normal", title: "Normal"));
        Add(agent, Conv("cron", source: ConversationSource.Cron, title: "Nightly"));
        Add(agent, Conv("hook", source: ConversationSource.Webhook, title: "Inbound Hook"));
        BuildStore([agent], "quill");

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "quill"));

        foreach (var testId in new[]
                 {
                     "pinned-group-toggle", "conversations-group-toggle",
                     "cron-group-toggle", "webhook-group-toggle"
                 })
        {
            Assert.Empty(cut.FindAll($"[data-testid='{testId}']"));
        }

        // The sections are nonetheless really there - so the assertion above is about the missing
        // affordance, not about a missing nav.
        Assert.Equal(3, cut.Find("select.conv-select").QuerySelectorAll("optgroup").Length);
    }

    [Fact]
    public void Mobile_navigation_persists_no_section_collapse_state()
    {
        // Desktop's four botnexus-*-collapsed keys stay desktop-only. Mobile must not read or write
        // them: a shared key would let one form factor collapse the other's nav, and a mobile-only
        // key would persist state nothing can ever read back.
        var agent = Agent();
        Add(agent, Conv("normal", title: "Normal"));
        Add(agent, Conv("cron", source: ConversationSource.Cron, title: "Nightly"));
        Add(agent, Conv("hook", source: ConversationSource.Webhook, title: "Inbound Hook"));
        BuildStore([agent], "quill");

        _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "quill"));

        var storageKeys = _ctx.JSInterop.Invocations
            .Where(i => i.Identifier is "localStorage.getItem" or "localStorage.setItem")
            .SelectMany(i => i.Arguments)
            .OfType<string>()
            .ToList();

        Assert.DoesNotContain(storageKeys, k => k.Contains("-collapsed", StringComparison.Ordinal));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

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
        ConversationSource source = ConversationSource.Channel,
        DateTimeOffset? updated = null,
        string? title = null,
        string status = "Active")
        => new()
        {
            ConversationId = id,
            IsPinned = isPinned,
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

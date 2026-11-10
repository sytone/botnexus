using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ConversationSidebarGroupsTests
{
    [Theory]
    [InlineData(true, ConversationSource.Cron, true, true, null)]
    [InlineData(true, ConversationSource.Cron, true, false, BuiltInConversationSection.Pinned)]
    [InlineData(false, ConversationSource.Webhook, true, false, BuiltInConversationSection.Scheduled)]
    [InlineData(false, ConversationSource.Cron, false, false, BuiltInConversationSection.Scheduled)]
    [InlineData(false, ConversationSource.Webhook, false, false, BuiltInConversationSection.Webhooks)]
    [InlineData(false, ConversationSource.Channel, false, false, BuiltInConversationSection.Conversations)]
    [InlineData(false, ConversationSource.Webhook, false, true, null)]
    public void Project_Precedence_IsExactlyOneWithoutChangingFacts(bool pinned, ConversationSource source, bool mapped, bool assigned, BuiltInConversationSection? expected)
    {
        var row = new ConversationState { ConversationId = "c", Source = source, IsPinned = pinned };
        var before = row.Project(SelectionSource.UserClick);
        var groups = ConversationSidebarGroups.Project("a", [row], "a", [Section()],
            assigned ? new Dictionary<string, string> { ["c"] = "s" } : new Dictionary<string, string>(),
            null, SelectionSource.UserClick, mapped ? new HashSet<string> { "c" } : null);
        var home = Assert.Single(groups, g => g.Conversations.Count > 0);
        Assert.Same(row, Assert.Single(home.Conversations));
        Assert.Equal(expected, home.Identity.System);
        Assert.Equal(assigned ? "s" : null, home.Identity.SectionId);
        Assert.Equal(before, row.Project(SelectionSource.UserClick));
        Assert.Equal(pinned, row.IsPinned);
        Assert.Equal(source, row.Source);
        Assert.Equal(5, groups.Count);
    }

    [Theory]
    [InlineData("a", "missing", "a")]
    [InlineData("a", "s", "foreign")]
    [InlineData("b", "s", "a")]
    public void Project_InvalidOrForeignAssignment_UsesNaturalHome(string snapshotAgent, string target, string sectionAgent)
    {
        var row = new ConversationState { ConversationId = "c", IsPinned = true };
        var groups = ConversationSidebarGroups.Project("a", [row], snapshotAgent, [Section(sectionAgent)],
            new Dictionary<string, string> { ["c"] = target }, null, SelectionSource.UserClick);
        Assert.Equal(BuiltInConversationSection.Pinned, Assert.Single(groups, g => g.Conversations.Count > 0).Identity.System);
    }

    [Fact]
    public void Project_OnlyActualEligibleRows_GeneralFilterDoesNotHideOtherHomes()
    {
        var rows = new[]
        {
            new ConversationState { ConversationId = "general" },
            new ConversationState { ConversationId = "custom" },
            new ConversationState { ConversationId = "pin", IsPinned = true },
            new ConversationState { ConversationId = "cron", Source = ConversationSource.Cron },
            new ConversationState { ConversationId = "hook", Source = ConversationSource.Webhook },
            new ConversationState { ConversationId = "hidden", Visibility = ConversationVisibility.InternalHidden },
            new ConversationState { ConversationId = "archived", Status = "Archived" },
        };
        var assignments = new Dictionary<string, string> { ["custom"] = "s", ["hidden"] = "s", ["archived"] = "s", ["unloaded"] = "s" };
        var groups = ConversationSidebarGroups.Project("a", rows, "a", [Section()], assignments,
            [new(SectionId: "s"), new(System: BuiltInConversationSection.Webhooks)], SelectionSource.UserClick,
            generalFilter: _ => false);
        Assert.Equal(5, groups.Count);
        Assert.Equal("s", groups[0].Identity.SectionId);
        Assert.Equal(new[] { "cron", "custom", "hook", "pin" }, groups.SelectMany(g => g.Conversations).Select(c => c.ConversationId).Order());
        Assert.Empty(groups.Single(g => g.Identity.System == BuiltInConversationSection.Conversations).Conversations);
        Assert.True(groups[0].Section?.IsCollapsed);
    }

    [Fact]
    public void Reconcile_MixedReads_PrunesStaleAndForeign_AppendsMissing_PreservesAllBuiltins()
    {
        var result = ConversationSidebarGroups.Reconcile("a", new SectionListDto
        {
            Sections = [Section(), Section("foreign", "f")],
            Assignments = new Dictionary<string, string> { ["c"] = "deleted", ["d"] = "f" },
            GroupOrder = [new(System: BuiltInConversationSection.Webhooks), new(SectionId: "deleted"), new(System: BuiltInConversationSection.Webhooks)],
        });
        Assert.True(result.RequiresRefresh);
        Assert.Single(result.Sections);
        Assert.Empty(result.Assignments);
        Assert.Equal(5, result.GroupOrder.Count);
        Assert.Equal(4, result.GroupOrder.Count(g => g.System is not null));
        Assert.Equal(BuiltInConversationSection.Webhooks, result.GroupOrder[0].System);
        Assert.Equal("s", result.GroupOrder[^1].SectionId);
    }

    [Fact]
    public void Project_EmptyCollapsedFilteredGroups_KeepFullCanonicalOrder()
    {
        var order = new SectionGroupOrderDto[] { new(SectionId: "s"), new(System: BuiltInConversationSection.Webhooks), new(System: BuiltInConversationSection.Scheduled), new(System: BuiltInConversationSection.Conversations), new(System: BuiltInConversationSection.Pinned) };
        var groups = ConversationSidebarGroups.Project("a", [], "a", [Section()], new Dictionary<string, string>(), order, SelectionSource.UserClick);
        Assert.Equal(order, groups.Select(g => g.Identity));
        Assert.All(groups, g => Assert.Empty(g.Conversations));
        Assert.Equal("Pinned", groups[0].Label); // A name never changes custom identity.
        Assert.Null(groups[0].Identity.System);
    }

    [Fact]
    public void Project_ReadOnlyAndDefaultPlacement_DoesNotGrantMovePermissionOrChangePicker()
    {
        var row = new ConversationState { ConversationId = "c", Source = ConversationSource.Webhook, IsDefault = true, IsPinned = true };
        var groups = ConversationSidebarGroups.Project("a", [row], "a", [Section()], new Dictionary<string, string> { ["c"] = "s" }, null, SelectionSource.UserClick);
        Assert.Equal("s", Assert.Single(groups, g => g.Conversations.Count > 0).Identity.SectionId);
        var actions = ConversationActionDefinitions.Create(new(true, row.IsDefault, row.Project(SelectionSource.UserClick).IsReadOnly, false, row.IsPinned, []), ConversationActionCallbacks.NoOp);
        Assert.Empty(actions);
        Assert.Equal(PortalConversationGrouping.PinnedLabel, Assert.Single(PortalConversationGrouping.ForPicker([row], SelectionSource.UserClick)).Label);
        var writableDefault = ConversationActionDefinitions.Create(new(true, true, false, false, false, []), ConversationActionCallbacks.NoOp);
        Assert.DoesNotContain(writableDefault, a => a.Id.StartsWith("move-", StringComparison.Ordinal));
    }

    [Fact]
    public void Reconcile_PresentCustomSubsequenceMismatch_UsesCanonicalOrderAndRequiresRefresh()
    {
        var first = Section(id: "first");
        first.Order = 0;
        var second = Section(id: "second");
        second.Order = 1;
        var order = new SectionGroupOrderDto[]
        {
            new(System: BuiltInConversationSection.Webhooks), new(SectionId: "second"),
            new(System: BuiltInConversationSection.Pinned), new(SectionId: "first"),
            new(System: BuiltInConversationSection.Scheduled), new(System: BuiltInConversationSection.Conversations),
        };
        var assignments = new Dictionary<string, string> { ["c"] = "first" };
        var snapshot = new SectionListDto { Sections = [first, second], Assignments = assignments, GroupOrder = order };
        var result = ConversationSidebarGroups.Reconcile("a", snapshot);
        Assert.True(result.RequiresRefresh); // All identities are present: only the custom subsequence disagrees.
        Assert.Equal(order, result.GroupOrder);
        Assert.Equal(new[] { "second", "first" }, result.Sections.Select(s => s.SectionId));
        Assert.Equal("first", result.Assignments["c"]);
        Assert.Equal(new[] { "first", "second" }, snapshot.Sections.Select(s => s.SectionId));
        Assert.Equal(new[] { 0, 1 }, snapshot.Sections.Select(s => s.Order));
        Assert.Equal(order, snapshot.GroupOrder);
        Assert.Single(assignments);

        // The same owned records are valid legacy data when canonical order is absent.
        snapshot.GroupOrder = null;
        var legacy = ConversationSidebarGroups.Reconcile("a", snapshot);
        Assert.False(legacy.RequiresRefresh);
        Assert.Equal(new[] { "first", "second" }, legacy.Sections.Select(s => s.SectionId));
        Assert.Equal(6, legacy.GroupOrder.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Project_DuplicateActualRows_EmitsOneIdentityInOneHomeWithoutMutatingRows(bool matchingAgent)
    {
        var duplicate = new ConversationState { ConversationId = "duplicate", IsPinned = true };
        var other = new ConversationState { ConversationId = "other", Source = ConversationSource.Webhook };
        var before = duplicate.Project(SelectionSource.UserClick);
        var rows = new[] { duplicate, duplicate, other, other };
        var assignments = new Dictionary<string, string> { ["duplicate"] = "s", ["not-loaded"] = "s" };
        var groups = ConversationSidebarGroups.Project("a", rows, matchingAgent ? "a" : "b", [Section()], assignments, null, SelectionSource.UserClick);
        var emitted = groups.SelectMany(g => g.Conversations).ToArray();
        Assert.Equal(new[] { "duplicate", "other" }, emitted.Select(r => r.ConversationId).Order());
        Assert.Same(duplicate, Assert.Single(emitted, r => r.ConversationId == "duplicate"));
        Assert.Same(other, Assert.Single(emitted, r => r.ConversationId == "other"));
        var home = Assert.Single(groups, g => g.Conversations.Contains(duplicate));
        Assert.Equal(matchingAgent ? "s" : null, home.Identity.SectionId);
        Assert.Equal(matchingAgent ? (BuiltInConversationSection?)null : BuiltInConversationSection.Pinned, home.Identity.System);
        Assert.Equal(matchingAgent ? 5 : 4, groups.Count);
        Assert.Equal(before, duplicate.Project(SelectionSource.UserClick));
        Assert.True(duplicate.IsPinned);
        Assert.Equal(ConversationSource.Webhook, other.Source);
        Assert.Equal(4, rows.Length);
        Assert.Equal("s", assignments["not-loaded"]);
    }

    [Fact]
    public void Project_DistinctInstancesWithSameActualIdentity_AreDeduplicatedAcrossNaturalHomes()
    {
        var first = new ConversationState { ConversationId = "same", IsPinned = true };
        var second = new ConversationState { ConversationId = "same", Source = ConversationSource.Webhook };
        var groups = ConversationSidebarGroups.Project("a", [first, second], "a", [], new Dictionary<string, string>(), null, SelectionSource.UserClick);
        var emitted = Assert.Single(groups.SelectMany(g => g.Conversations));
        Assert.Equal("same", emitted.ConversationId);
        Assert.True(ReferenceEquals(first, emitted) || ReferenceEquals(second, emitted));
        Assert.Single(groups, g => g.Conversations.Count > 0);
        Assert.Equal(4, groups.Count);
        Assert.True(first.IsPinned);
        Assert.Equal(ConversationSource.Webhook, second.Source);
    }

    private static SectionDto Section(string agent = "a", string id = "s") => new() { AgentId = agent, SectionId = id, Name = "Pinned", IsCollapsed = true };
}

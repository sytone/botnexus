using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>One canonical peer and its actual eligible conversation rows, including empty groups.</summary>
public sealed record ConversationSidebarGroup(
    SectionGroupOrderDto Identity, string Label, SectionDto? Section, IReadOnlyList<ConversationState> Conversations);

/// <summary>Safe read projection, not a repair payload. A mismatch requires an explicit new GET.</summary>
public sealed record ReconciledConversationSections(
    IReadOnlyList<SectionDto> Sections, IReadOnlyDictionary<string, string> Assignments,
    IReadOnlyList<SectionGroupOrderDto> GroupOrder, bool RequiresRefresh);

/// <summary>
/// Resolves each eligible row once: custom override, pinned, mapped/projected scheduled, webhook,
/// general. Placement never changes source, pinning, read-only or action eligibility. Mobile picker
/// semantics remain in PortalConversationGrouping.ForPicker.
/// </summary>
public static class ConversationSidebarGroups
{
    private static readonly BuiltInConversationSection[] BuiltIns =
    [BuiltInConversationSection.Pinned, BuiltInConversationSection.Conversations,
        BuiltInConversationSection.Scheduled, BuiltInConversationSection.Webhooks];

    /// <summary>
    /// Reconciles independently-read constituents against owned available custom records, without
    /// writing repairs. Null order is valid legacy data; a present incomplete order is a mismatch.
    /// </summary>
    public static ReconciledConversationSections Reconcile(string agentId, SectionListDto snapshot)
    {
        var mismatch = false;
        var sections = new List<SectionDto>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in snapshot.Sections.OrderBy(s => s?.Order ?? int.MaxValue))
        {
            if (section is null || section.AgentId != agentId || string.IsNullOrWhiteSpace(section.SectionId) || !ids.Add(section.SectionId))
            {
                mismatch = true;
                continue;
            }
            // Own the snapshot so collapse callers cannot mutate the HTTP DTO beneath reconciliation.
            sections.Add(new SectionDto { AgentId = section.AgentId, SectionId = section.SectionId,
                Name = section.Name, Order = section.Order, IsCollapsed = section.IsCollapsed });
        }
        var assignments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in snapshot.Assignments)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || !ids.Contains(pair.Value))
                mismatch = true;
            else
                assignments.Add(pair.Key, pair.Value);
        }
        var order = new List<SectionGroupOrderDto>();
        var seen = new HashSet<SectionGroupOrderDto>();
        if (snapshot.GroupOrder is not null)
        {
            foreach (var entry in snapshot.GroupOrder)
            {
                if (!IsValid(entry, ids) || entry is null || !seen.Add(entry))
                    mismatch = true;
                else
                    order.Add(entry);
            }
        }
        foreach (var entry in BuiltIns.Select(s => new SectionGroupOrderDto(System: s))
            .Concat(sections.Select(s => new SectionGroupOrderDto(SectionId: s.SectionId))))
        {
            if (!seen.Add(entry)) continue;
            if (snapshot.GroupOrder is not null) mismatch = true;
            order.Add(entry);
        }
        // GroupOrder is canonical when supplied, even if legacy Order came from an earlier read.
        var customIds = order.Where(e => e.SectionId is not null).Select(e => e.SectionId).ToArray();
        if (snapshot.GroupOrder is not null && !sections.Select(s => s.SectionId).SequenceEqual(customIds))
            mismatch = true;
        var byId = sections.ToDictionary(s => s.SectionId, StringComparer.Ordinal);
        sections = customIds.OfType<string>().Select(id => byId[id]).ToList();
        return new(sections.AsReadOnly(), assignments, order.AsReadOnly(), mismatch);
    }

    /// <summary>
    /// Builds all peers independent of counts, filter or collapse. The caller supplies only the
    /// active agent's actual conversation collection (ConversationState has no owner field).
    /// Unloaded/hidden/archived assignment ids never manufacture rows. The optional activity filter
    /// applies only after resolving a row to general Conversations, never to its other homes.
    /// </summary>
    public static IReadOnlyList<ConversationSidebarGroup> Project(
        string agentId, IEnumerable<ConversationState> conversations, string snapshotAgentId,
        IReadOnlyList<SectionDto> sections, IReadOnlyDictionary<string, string> assignments,
        IReadOnlyList<SectionGroupOrderDto>? groupOrder, SelectionSource selectionSource,
        IReadOnlySet<string>? cronConversationIds = null, Func<ConversationState, bool>? generalFilter = null)
    {
        var matching = agentId == snapshotAgentId;
        var snapshot = Reconcile(agentId, new SectionListDto
        {
            Sections = matching ? sections : [], Assignments = matching ? assignments : new Dictionary<string, string>(),
            GroupOrder = matching ? groupOrder : null,
        });
        var groups = snapshot.GroupOrder.ToDictionary(e => e, _ => new List<ConversationState>());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in conversations.Where(PortalListOrdering.IsUserFacingConversation)
            .Where(c => !PortalListOrdering.IsArchivedConversation(c)).OrderForDisplay())
        {
            if (string.IsNullOrWhiteSpace(row.ConversationId) || !seen.Add(row.ConversationId)) continue;
            SectionGroupOrderDto home;
            if (snapshot.Assignments.TryGetValue(row.ConversationId, out var custom))
                home = new(SectionId: custom);
            else
                home = new(System: row.IsPinned ? BuiltInConversationSection.Pinned
                    : PortalConversationGrouping.IsScheduled(row, selectionSource, cronConversationIds) ? BuiltInConversationSection.Scheduled
                    : PortalConversationGrouping.IsWebhook(row, selectionSource) ? BuiltInConversationSection.Webhooks
                    : BuiltInConversationSection.Conversations);
            if (home.System == BuiltInConversationSection.Conversations && generalFilter is not null && !generalFilter(row)) continue;
            groups[home].Add(row);
        }
        var customs = snapshot.Sections.ToDictionary(s => s.SectionId, StringComparer.Ordinal);
        return snapshot.GroupOrder.Select(identity =>
        {
            var section = identity.SectionId is { } id ? customs[id] : null;
            return new ConversationSidebarGroup(identity, section?.Name ?? identity.System?.ToString() ?? string.Empty,
                section, groups[identity].AsReadOnly());
        }).ToArray();
    }

    private static bool IsValid(SectionGroupOrderDto? entry, IReadOnlySet<string> ids) => entry is not null &&
        ((entry.System is { } system && Enum.IsDefined(system) && entry.SectionId is null)
         || (entry.System is null && entry.SectionId is { } id && ids.Contains(id)));
}

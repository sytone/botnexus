namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// A single labelled group of conversations in a portal conversation picker or list.
/// </summary>
/// <param name="Label">The human-readable group heading (also used as the <c>optgroup</c> label).</param>
/// <param name="Conversations">The conversations in the group, already in display order. Never empty.</param>
public sealed record PortalConversationGroup(string Label, IReadOnlyList<ConversationState> Conversations);

/// <summary>
/// Shared partition of an agent's conversations into the Pinned / Conversations / Scheduled groups
/// the desktop sidebar renders, so a picker on any form factor can expose the same structure without
/// re-deriving the rules.
/// </summary>
/// <remarks>
/// <para>
/// Fixes #2327: the mobile picker rendered one flat option list while <c>MainLayout.razor</c> already
/// grouped the same conversations. The grouping inputs are exactly the desktop's: pinned is
/// <see cref="ConversationState.IsPinned"/>, and scheduled is the
/// <see cref="ConversationRenderProjection.Group"/> projection over the immutable server-supplied
/// <c>(Kind, Source)</c> origin pair (never a session-id prefix probe - epic #2300 / #2305).
/// </para>
/// <para>
/// Pinning wins over scheduling, matching the sidebar, which filters pinned conversations out first
/// and excludes them from the cron group - so a pinned scheduled run appears exactly once, at the
/// top. Groups with no members are omitted rather than emitted empty, so a caller can bind directly
/// without a per-group emptiness check.
/// </para>
/// <para>
/// This is additive and purely functional: the desktop sidebar keeps its own rendering (it needs
/// collapse state, filter bars and per-row affordances a native picker cannot express) and is
/// deliberately left untouched.
/// </para>
/// </remarks>
public static class PortalConversationGrouping
{
    /// <summary>Heading for pinned conversations, surfaced first.</summary>
    public const string PinnedLabel = "Pinned";

    /// <summary>Heading for ordinary, non-pinned, non-scheduled conversations.</summary>
    public const string ConversationsLabel = "Conversations";

    /// <summary>Heading for schedule-driven (cron/heartbeat) runs.</summary>
    public const string ScheduledLabel = "Scheduled";

    /// <summary>
    /// Groups <paramref name="conversations"/> for a picker: Pinned, then Conversations, then
    /// Scheduled. Each group's members are sorted with the shared
    /// <see cref="PortalListOrdering.OrderForDisplay(IEnumerable{ConversationState})"/> comparator so
    /// ordering inside a group stays identical to the ungrouped list. Empty groups are omitted.
    /// </summary>
    /// <param name="conversations">
    /// The conversations to group. Filtering (e.g. to <c>Status == "Active"</c>) stays with the call
    /// site, exactly as with <see cref="PortalListOrdering"/>.
    /// </param>
    /// <param name="selectionSource">The current view-selection source, fed to the render projection.</param>
    /// <returns>The non-empty groups in display order.</returns>
    public static IReadOnlyList<PortalConversationGroup> ForPicker(
        IEnumerable<ConversationState> conversations,
        SelectionSource selectionSource)
    {
        ArgumentNullException.ThrowIfNull(conversations);

        var all = conversations.ToList();
        var pinned = all.Where(c => c.IsPinned).ToList();
        var scheduled = all
            .Where(c => !c.IsPinned && c.Project(selectionSource).Group == ConversationListGroup.Scheduled)
            .ToList();
        var normal = all.Where(c => !c.IsPinned && !scheduled.Contains(c)).ToList();

        var groups = new List<PortalConversationGroup>(3);
        AddIfAny(groups, PinnedLabel, pinned);
        AddIfAny(groups, ConversationsLabel, normal);
        AddIfAny(groups, ScheduledLabel, scheduled);
        return groups;
    }

    private static void AddIfAny(List<PortalConversationGroup> groups, string label, List<ConversationState> members)
    {
        if (members.Count == 0) return;
        groups.Add(new PortalConversationGroup(label, members.OrderForDisplay().ToList()));
    }
}

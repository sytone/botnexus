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
/// grouped the same conversations.
/// </para>
/// <para>
/// <b>Signals consulted (#3073).</b> Exactly two, and both are required:
/// <list type="number">
///   <item>
///     the <see cref="ConversationRenderProjection.Group"/> projection over the immutable
///     server-supplied <c>(Kind, Source)</c> origin pair (never a session-id prefix probe - epic
///     #2300 / #2305); and
///   </item>
///   <item>
///     the authoritative cron-job to conversation-id map from <c>GET /api/cron</c>, passed in by
///     the caller.
///   </item>
/// </list>
/// The second is not redundant. <c>Source</c> is write-once (#2304), so a conversation created
/// through a channel binding and LATER adopted by a cron job keeps <c>Source = Channel</c>
/// permanently and can never be identified from the projection alone. #2327 extracted only clause 1
/// from <c>MainLayout.IsCronConversation</c>, which mis-grouped 61 conversations on the reporting
/// instance; #3073 restored the missing clause here and made the desktop consume this helper, so
/// there is now exactly one cron-classification predicate in the client. That is the mechanism by
/// which the form factors agree - not an inherent property, which is what an earlier revision of
/// this comment wrongly claimed.
/// </para>
/// <para>
/// The cron id set is a parameter rather than a fetch: this type stays purely functional, callers
/// keep their single existing <c>CronApiClient.ListAsync()</c> call, and a failed fetch degrades to
/// an empty set (i.e. projection-only grouping) instead of throwing.
/// </para>
/// <para>
/// Pinning wins over scheduling, matching the sidebar, which filters pinned conversations out first
/// and excludes them from the cron group - so a pinned scheduled run appears exactly once, at the
/// top. Groups with no members are omitted rather than emitted empty, so a caller can bind directly
/// without a per-group emptiness check.
/// </para>
/// <para>
/// The desktop sidebar keeps its own rendering (it needs collapse state, filter bars and per-row
/// affordances a native picker cannot express) but shares the classification via
/// <see cref="IsScheduled"/>.
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

    /// <summary>Heading for inbound-webhook runs (#2709), matching the desktop sidebar's 4th section.</summary>
    public const string WebhooksLabel = "Webhooks";

    /// <summary>
    /// Groups <paramref name="conversations"/> for a picker: Pinned, then Conversations, then
    /// Scheduled, then Webhooks. Each group's members are sorted with the shared
    /// <see cref="PortalListOrdering.OrderForDisplay(IEnumerable{ConversationState})"/> comparator so
    /// ordering inside a group stays identical to the ungrouped list. Empty groups are omitted.
    /// </summary>
    /// <param name="conversations">
    /// The conversations to group. Filtering (e.g. to <c>Status == "Active"</c>) stays with the call
    /// site, exactly as with <see cref="PortalListOrdering"/>.
    /// </param>
    /// <param name="selectionSource">The current view-selection source, fed to the render projection.</param>
    /// <param name="cronConversationIds">
    /// The authoritative cron-job to conversation-id map from <c>GET /api/cron</c>, projected with
    /// <see cref="CronConversationIds"/>. <see langword="null"/> or empty (a failed fetch) degrades
    /// to projection-only grouping rather than throwing or emptying the picker.
    /// </param>
    /// <returns>The non-empty groups in display order.</returns>
    public static IReadOnlyList<PortalConversationGroup> ForPicker(
        IEnumerable<ConversationState> conversations,
        SelectionSource selectionSource,
        IReadOnlySet<string>? cronConversationIds = null)
    {
        ArgumentNullException.ThrowIfNull(conversations);

        var all = conversations.ToList();
        var pinned = all.Where(c => c.IsPinned).ToList();
        var scheduled = all
            .Where(c => !c.IsPinned && IsScheduled(c, selectionSource, cronConversationIds))
            .ToList();
        // #2709: the 4th desktop section. Subtraction order mirrors MainLayout.razor exactly -
        // `!IsPinned && !IsCron && IsWebhook` - so precedence is Pinned > Scheduled > Webhooks >
        // Conversations and a conversation lands in exactly one group.
        var webhooks = all
            .Where(c => !c.IsPinned && !scheduled.Contains(c) && IsWebhook(c, selectionSource))
            .ToList();
        var normal = all
            .Where(c => !c.IsPinned && !scheduled.Contains(c) && !webhooks.Contains(c))
            .ToList();

        var groups = new List<PortalConversationGroup>(4);
        AddIfAny(groups, PinnedLabel, pinned);
        AddIfAny(groups, ConversationsLabel, normal);
        AddIfAny(groups, ScheduledLabel, scheduled);
        AddIfAny(groups, WebhooksLabel, webhooks);
        return groups;
    }

    /// <summary>
    /// The ONE cron-classification predicate in the client (#3073, AC3). A conversation is
    /// scheduled if the render projection says so <em>or</em> if its id is the target of a cron job.
    /// </summary>
    /// <remarks>
    /// The second clause exists because <c>ConversationState.Source</c> is write-once (#2304): a
    /// channel-created conversation later adopted by a cron job is invisible to the projection
    /// forever. Do not add a third variant of this rule - call this.
    /// </remarks>
    /// <param name="conversation">The conversation to classify.</param>
    /// <param name="selectionSource">The current view-selection source, fed to the render projection.</param>
    /// <param name="cronConversationIds">
    /// Cron-mapped conversation ids; <see langword="null"/> or empty means "unknown", which falls
    /// back to the projection alone.
    /// </param>
    public static bool IsScheduled(
        ConversationState conversation,
        SelectionSource selectionSource,
        IReadOnlySet<string>? cronConversationIds)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        return conversation.Project(selectionSource).Group == ConversationListGroup.Scheduled
            || (cronConversationIds is { Count: > 0 }
                && conversation.ConversationId is { Length: > 0 } id
                && cronConversationIds.Contains(id));
    }

    /// <summary>
    /// The ONE webhook-classification predicate in the client (#2709). Membership reads the same
    /// typed provenance projection the desktop sidebar's <c>IsWebhookConversation</c> reads - the
    /// immutable server-supplied <c>(Kind, Source)</c> pair - never a title or session-id prefix.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="IsScheduled"/> there is no second clause: nothing can "adopt" an existing
    /// conversation into webhook origin the way a cron job can adopt one, so the projection is
    /// complete on its own.
    /// </remarks>
    /// <param name="conversation">The conversation to classify.</param>
    /// <param name="selectionSource">The current view-selection source, fed to the render projection.</param>
    public static bool IsWebhook(ConversationState conversation, SelectionSource selectionSource)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return conversation.Project(selectionSource).Group == ConversationListGroup.Automated;
    }

    /// <summary>
    /// Projects a cron job list into the case-insensitive conversation-id set
    /// <see cref="IsScheduled"/> consumes, matching the desktop sidebar's original projection.
    /// </summary>
    /// <param name="jobs">
    /// The jobs from <c>CronApiClient.ListAsync()</c>. <see langword="null"/> (a failed fetch)
    /// yields an empty set, which degrades grouping to projection-only.
    /// </param>
    public static IReadOnlySet<string> CronConversationIds(IEnumerable<CronJobDto>? jobs)
        => jobs is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : jobs
                .Where(j => !string.IsNullOrEmpty(j.ConversationId))
                .Select(j => j.ConversationId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void AddIfAny(List<PortalConversationGroup> groups, string label, List<ConversationState> members)
    {
        if (members.Count == 0) return;
        groups.Add(new PortalConversationGroup(label, members.OrderForDisplay().ToList()));
    }
}

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Shared ordering for the portal agent dropdown and conversation list so the desktop
/// (<c>MainLayout.razor</c>) and mobile (<c>Chat.razor</c>) views render the same order and
/// cannot drift apart. These are pure ordering helpers — they do not change which agents or
/// conversations are shown (the call site keeps its own filtering); they only impose a
/// deterministic, form-factor-consistent sort.
/// </summary>
/// <remarks>
/// Fixes #1480: the mobile views previously enumerated agents in raw
/// <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/> order and ordered
/// conversations by <see cref="ConversationState.UpdatedAt"/> only, diverging from desktop.
/// </remarks>
public static class PortalListOrdering
{
    /// <summary>
    /// Order agents the way the desktop agent dropdown does: platform built-ins after user-created
    /// agents, then alphabetically by display name. Matches <c>MainLayout.razor</c>'s
    /// <c>.OrderBy(IsBuiltIn).ThenBy(DisplayName)</c>.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, AgentState>> OrderForDisplay(
        this IEnumerable<KeyValuePair<string, AgentState>> agents)
        => agents
            .OrderBy(kv => kv.Value.IsBuiltIn)
            .ThenBy(kv => kv.Value.DisplayName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Order conversations the way the desktop conversation list does: the agent's default
    /// conversation first, then most-recently-updated. Matches <c>MainLayout.razor</c>'s
    /// <c>.OrderByDescending(IsDefault).ThenByDescending(UpdatedAt)</c>. The auto-select logic and
    /// the rendered list must share this so the top-of-list conversation is the one auto-selected.
    /// </summary>
    public static IEnumerable<ConversationState> OrderForDisplay(this IEnumerable<ConversationState> conversations)
        => conversations
            .OrderByDescending(c => c.IsDefault)
            .ThenByDescending(c => c.UpdatedAt);

    /// <summary>
    /// The single client-wide "may the user see this conversation at all?" predicate (#2340).
    /// Reads the first-class, server-stamped, write-once <see cref="ConversationVisibility"/> rather
    /// than probing the conversation id for an <c>internal:</c> prefix - the origin-inference fence is
    /// absolute. Promoted out of <c>MainLayout.razor</c> in #3218 so the sidebar list and the
    /// cold-start route resolver cannot disagree about what counts as a user conversation.
    /// </summary>
    public static bool IsUserFacingConversation(ConversationState conversation) =>
        conversation.Visibility != ConversationVisibility.InternalHidden;

    /// <summary>
    /// True when the conversation is archived, i.e. not a candidate for the cold-start redirect.
    /// Status is a free-form server string, so the comparison is ordinal-ignore-case rather than an
    /// exact match on a casing the server never promised.
    /// </summary>
    public static bool IsArchivedConversation(ConversationState conversation) =>
        string.Equals(conversation.Status, "Archived", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// #3218 AC1-AC3: resolve the conversation a <b>cold</b> circuit should land on for an agent-only
    /// route, when this circuit's MRU holds nothing for that agent. Candidates are the agent's
    /// user-facing, non-archived conversations; a pinned conversation wins over a more recently
    /// updated unpinned one (AC2), and ties break on most-recently-updated.
    /// </summary>
    /// <remarks>
    /// This deliberately does <b>not</b> lead with <see cref="ConversationState.IsDefault"/> the way
    /// <see cref="OrderForDisplay(IEnumerable{ConversationState})"/> does. The sidebar list pins the
    /// agent's default conversation to the top as a navigational affordance; cold start is answering a
    /// different question - "where was this user working?" - and AC2 makes the user's own pin the
    /// stronger signal. The two orderings are intentionally distinct, which is why this is its own
    /// named method rather than a reuse of the display sort.
    /// </remarks>
    /// <param name="conversations">The routed agent's conversations.</param>
    /// <returns>The conversation to redirect to, or <c>null</c> when the agent has no eligible
    /// conversation - in which case the caller must NOT redirect (AC5).</returns>
    public static ConversationState? ResolveColdStartConversation(IEnumerable<ConversationState> conversations)
        => conversations
            .Where(IsUserFacingConversation)
            .Where(c => !IsArchivedConversation(c))
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.UpdatedAt)
            .FirstOrDefault();

    /// <summary>
    /// #3218: build the portal route for an agent+conversation from the <b>declared</b> client kind
    /// (<c>IPortalLoadService.ClientKind</c>) rather than an ambient viewport probe, so desktop and
    /// mobile each resolve to their own shell's path shape from one stated input. The desktop and
    /// mobile portals are separate apps served at <c>/</c> and <c>/mobile/</c>.
    /// </summary>
    /// <remarks>
    /// The mobile shell's own route step is #3213; this helper only supplies the shape, and callers
    /// on the desktop shell pass <c>"desktop"</c>. Any unrecognised kind falls back to the desktop
    /// shape rather than throwing: a routing helper must never be the thing that breaks navigation.
    /// </remarks>
    public static string BuildConversationRoute(string? clientKind, string agentId, string? conversationId)
    {
        var encodedAgentId = Uri.EscapeDataString(agentId);
        var relative = string.IsNullOrWhiteSpace(conversationId)
            ? $"agent/{encodedAgentId}"
            : $"agent/{encodedAgentId}/conversation/{Uri.EscapeDataString(conversationId)}";

        return string.Equals(clientKind, "mobile", StringComparison.OrdinalIgnoreCase)
            ? "/mobile/" + relative
            : relative;
    }
}

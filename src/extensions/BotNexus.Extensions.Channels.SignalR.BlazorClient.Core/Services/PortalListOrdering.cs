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
}

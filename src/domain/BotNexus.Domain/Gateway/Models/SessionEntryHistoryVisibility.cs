using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Defines which stored entries can appear in a user-visible transcript, independently of storage
/// or transport. Visibility is a prerequisite for unread participation, not an unread-count policy.
/// </summary>
public static class SessionEntryHistoryVisibility
{
    /// <summary>
    /// Returns whether history should project the entry. Folded entries remain reachable and
    /// compaction summaries remain visible markers; neither implies a new unread message.
    /// </summary>
    public static bool IsVisible(SessionEntry? entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsCrashSentinel)
            return false;

        if (entry.Role != MessageRole.Assistant)
            return true;

        // Deliberate no-op turns must never become transcript content, including legacy rows.
        if (string.Equals(entry.Content?.Trim(), "NO_REPLY", StringComparison.Ordinal))
            return false;

        // Thinking, attachments and tool identity are content even without a text bubble.
        return entry.IsCompactionSummary
            || !string.IsNullOrWhiteSpace(entry.Content)
            || !string.IsNullOrWhiteSpace(entry.ThinkingContent)
            || entry.OriginalContentParts is { Count: > 0 }
            || entry.ToolCallId is not null
            || entry.ToolName is not null;
    }
}

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Represents paginated session history response containing conversation entries.
/// </summary>
/// <param name="Offset">The zero-based offset for pagination.</param>
/// <param name="Limit">The maximum number of entries returned.</param>
/// <param name="TotalCount">The total number of entries in the session history.</param>
/// <param name="Entries">The list of session history entries.</param>
public sealed record SessionHistoryResponse(
    int Offset,
    int Limit,
    int TotalCount,
    IReadOnlyList<SessionEntry> Entries);

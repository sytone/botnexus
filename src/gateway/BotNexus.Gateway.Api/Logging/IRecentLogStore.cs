namespace BotNexus.Gateway.Api.Logging;

/// <summary>
/// Defines the contract for irecent log store.
/// </summary>
public interface IRecentLogStore
{
    /// <summary>
    /// Adds a log entry to the store.
    /// </summary>
    /// <param name="entry">The entry to store.</param>
    void Add(RecentLogEntry entry);

    /// <summary>
    /// Returns the most recent log entries.
    /// </summary>
    /// <param name="limit">The maximum number of entries to return.</param>
    /// <returns>A list of recent log entries.</returns>
    IReadOnlyList<RecentLogEntry> GetRecent(int limit);
}

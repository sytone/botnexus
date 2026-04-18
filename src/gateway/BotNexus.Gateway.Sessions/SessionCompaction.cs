using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Represents session compaction.
/// </summary>
public static class SessionCompaction
{
    /// <summary>
    /// Executes keep from last compaction.
    /// </summary>
    /// <param name="entries">The entries.</param>
    /// <returns>The keep from last compaction result.</returns>
    public static IReadOnlyList<SessionEntry> KeepFromLastCompaction(IEnumerable<SessionEntry> entries)
    {
        var materialized = entries.ToList();
        var lastCompactionIndex = materialized.FindLastIndex(static entry => entry.IsCompactionSummary);
        if (lastCompactionIndex < 0)
            return materialized;

        return materialized.GetRange(lastCompactionIndex, materialized.Count - lastCompactionIndex);
    }
}

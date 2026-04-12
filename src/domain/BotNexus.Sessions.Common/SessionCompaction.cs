using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Sessions.Common;

public static class SessionCompaction
{
    public static IReadOnlyList<SessionEntry> KeepFromLastCompaction(IEnumerable<SessionEntry> entries)
    {
        var materialized = entries.ToList();
        var lastCompactionIndex = materialized.FindLastIndex(static entry => entry.IsCompactionSummary);
        if (lastCompactionIndex < 0)
            return materialized;

        return materialized.GetRange(lastCompactionIndex, materialized.Count - lastCompactionIndex);
    }
}

using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Helpers for projecting and migrating session history with respect to compaction state.
/// After Phase 3a (#531) the canonical model is "preserve all entries in storage, mark older
/// summarised entries with <see cref="SessionEntry.IsHistory"/> = true, project to the LLM
/// elsewhere". This class owns the legacy-to-new forward migration applied at load time.
/// </summary>
public static class SessionCompaction
{
    /// <summary>
    /// Forward-migrates session histories written by the pre-Phase-3a compactor, which
    /// produced multiple <see cref="SessionEntry.IsCompactionSummary"/> rows on disk all
    /// with <see cref="SessionEntry.IsHistory"/> = false (the old code applied a load-time
    /// slice to hide them from the runtime). Returns a new list where every summary except
    /// the latest is marked <c>IsHistory = true</c>; non-summary entries are unchanged. The
    /// projection is idempotent — running it on already-migrated history is a no-op because
    /// older summaries are already <c>IsHistory = true</c> and are skipped.
    /// </summary>
    /// <param name="entries">Raw entries loaded from the session store.</param>
    /// <returns>A new list with legacy multi-summary state collapsed forward.</returns>
    public static List<SessionEntry> ApplyLegacyHistoryProjection(IEnumerable<SessionEntry> entries)
    {
        var materialized = entries.ToList();

        var activeSummaries = new List<int>();
        for (var i = 0; i < materialized.Count; i++)
        {
            if (materialized[i].IsCompactionSummary && !materialized[i].IsHistory)
                activeSummaries.Add(i);
        }

        if (activeSummaries.Count <= 1)
            return materialized;

        for (var i = 0; i < activeSummaries.Count - 1; i++)
        {
            var idx = activeSummaries[i];
            materialized[idx] = materialized[idx] with { IsHistory = true };
        }

        return materialized;
    }
}

using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Streaming;

/// <summary>
/// The measurement side of prompt caching: what share of the prompt each turn actually read back
/// from the provider's cache, accumulated per session.
/// </summary>
/// <remarks>
/// <para>
/// Every cache change is a hypothesis about a number nobody has looked at. Cache reads and writes
/// were already parsed and priced, but nothing aggregated them, so the one question that decides
/// whether any of this work pays -- "is the prefix staying stable across turns?" -- could not be
/// answered without scraping provider dashboards.
/// </para>
/// <para>
/// The totals ride the session metadata bag on the same last-writer-wins, no-schema-change path as
/// <c>lastProviderPromptTokens</c>, so a session's lifetime hit ratio is readable wherever the
/// session is, rather than only from logs that may not have been enabled at the time.
/// </para>
/// <para>
/// Interpreting the ratio: the first turn of any conversation is necessarily a cache write and
/// reads nothing, so a low ratio there is expected. A tool-using session that has not settled
/// above roughly 0.8 by the third turn is telling you the prefix is moving between requests --
/// volatile content ahead of the last breakpoint, a reordered tool array, or a compaction that
/// just reset the prefix.
/// </para>
/// </remarks>
public static class PromptCacheEfficiency
{
    /// <summary>Cumulative uncached input tokens billed at the full rate.</summary>
    public const string CumulativeInputMetadataKey = "cacheEfficiency.inputTokens";

    /// <summary>Cumulative tokens served from the provider's prompt cache.</summary>
    public const string CumulativeCacheReadMetadataKey = "cacheEfficiency.cacheReadTokens";

    /// <summary>Cumulative tokens written into the provider's prompt cache.</summary>
    public const string CumulativeCacheWriteMetadataKey = "cacheEfficiency.cacheWriteTokens";

    /// <summary>
    /// Share of one request's prompt that came back from the cache, or <c>null</c> when the
    /// provider reported no prompt tokens at all. Providers that do not report cache figures
    /// report zero for both, which is a real 0.0 rather than an absent measurement.
    /// </summary>
    public static double? TurnHitRatio(AgentResponseUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return HitRatio(usage.InputTokens ?? 0, usage.CacheRead ?? 0, usage.CacheWrite ?? 0);
    }

    /// <summary>
    /// Share of a session's whole prompt spend that came back from the cache, or <c>null</c> when
    /// nothing has been accumulated yet.
    /// </summary>
    public static double? SessionHitRatio(GatewaySession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var (input, cacheRead, cacheWrite) = ReadTotals(session);
        return HitRatio(input, cacheRead, cacheWrite);
    }

    /// <summary>
    /// Adds one completed request's prompt usage to the session totals.
    /// </summary>
    /// <returns><c>true</c> when the totals moved; otherwise <c>false</c>.</returns>
    public static bool Accumulate(GatewaySession session, AgentResponseUsage? usage)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (usage is null)
        {
            return false;
        }

        long input = usage.InputTokens ?? 0;
        long cacheRead = usage.CacheRead ?? 0;
        long cacheWrite = usage.CacheWrite ?? 0;

        if (input + cacheRead + cacheWrite <= 0)
        {
            return false;
        }

        var (priorInput, priorRead, priorWrite) = ReadTotals(session);

        session.Metadata[CumulativeInputMetadataKey] = priorInput + input;
        session.Metadata[CumulativeCacheReadMetadataKey] = priorRead + cacheRead;
        session.Metadata[CumulativeCacheWriteMetadataKey] = priorWrite + cacheWrite;
        return true;
    }

    /// <summary>
    /// Reads the accumulated totals. Absent or unreadable entries read as zero, so a session that
    /// predates this accounting simply starts from nothing rather than throwing.
    /// </summary>
    public static (long Input, long CacheRead, long CacheWrite) ReadTotals(GatewaySession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return (
            ReadCounter(session, CumulativeInputMetadataKey),
            ReadCounter(session, CumulativeCacheReadMetadataKey),
            ReadCounter(session, CumulativeCacheWriteMetadataKey));
    }

    private static double? HitRatio(long input, long cacheRead, long cacheWrite)
    {
        var total = input + cacheRead + cacheWrite;
        return total > 0 ? (double)cacheRead / total : null;
    }

    /// <summary>
    /// Tolerant of how the value came back from storage. A metadata bag typed
    /// <c>Dictionary&lt;string, object?&gt;</c> holds a boxed integer in memory but a
    /// <see cref="JsonElement"/> or a string once it has round-tripped through persistence, and a
    /// counter that silently reset on reload would make every long session look freshly started.
    /// </summary>
    private static long ReadCounter(GatewaySession session, string key)
    {
        if (session.Metadata is null ||
            !session.Metadata.TryGetValue(key, out var raw) ||
            raw is null)
        {
            return 0;
        }

        return raw switch
        {
            long l when l >= 0 => l,
            int i when i >= 0 => i,
            string s when long.TryParse(s, out var parsed) && parsed >= 0 => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt64(out var j) && j >= 0 => j,
            _ => 0
        };
    }
}

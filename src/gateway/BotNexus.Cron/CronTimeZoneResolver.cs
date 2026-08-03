namespace BotNexus.Cron;

/// <summary>
/// The single canonical definition of "how a cron timezone id resolves".
/// <para>
/// This type exists in exactly one place on purpose. Before issue #2748 the cron seam
/// carried three independent spellings of the same resolution: a private
/// <c>ResolveTimeZone(CronJob)</c> on <see cref="CronScheduler"/> (which fed the next-run
/// computation), a private <c>ResolveTimeZone(string)</c> on the model-facing cron tool,
/// and <c>TimeZoneHelper.Resolve</c> used by the heartbeat/agent-prompt actions and the
/// missed-run detector. The scheduler and tool variants performed a single
/// <see cref="TimeZoneInfo.FindSystemTimeZoneById(string)"/> with no Windows-to-IANA
/// conversion, so on a host whose timezone database only knows one spelling a perfectly
/// resolvable id silently degraded to UTC - and the job then fired at the wrong hour while
/// the actions that ran it reported the right one. A duplicated definition of resolution
/// IS the defect, so every cron call site must delegate here rather than re-implement.
/// </para>
/// <para>
/// Fail-safe direction: an unresolvable id degrades to <see cref="TimeZoneInfo.Utc"/>
/// rather than throwing, because this runs inside the scheduler loop and a throw would
/// stop all scheduling. A resolvable id must never become UTC - that is the bug.
/// </para>
/// </summary>
internal static class CronTimeZoneResolver
{
    /// <summary>
    /// Resolves a cron timezone id against the host timezone database, accepting either
    /// Windows or IANA spelling regardless of which family the host natively stores.
    /// Returns <see cref="TimeZoneInfo.Utc"/> for null/blank/"UTC" or an unresolvable id.
    /// </summary>
    internal static TimeZoneInfo Resolve(string? timezoneId)
        => Resolve(timezoneId, TimeZoneInfo.FindSystemTimeZoneById);

    /// <summary>
    /// Resolution against an explicit host-database lookup. The seam exists so tests can
    /// model a host that only knows IANA ids (Linux) or only Windows ids (Windows without
    /// ICU) - the two failure modes behind #2748 - which cannot both be reproduced on a
    /// single real machine.
    /// </summary>
    internal static TimeZoneInfo Resolve(string? timezoneId, Func<string, TimeZoneInfo> hostLookup)
    {
        ArgumentNullException.ThrowIfNull(hostLookup);

        if (string.IsNullOrWhiteSpace(timezoneId) ||
            timezoneId.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Utc;

        if (TryLookup(hostLookup, timezoneId, out var direct))
            return direct;

        // The host stores the other family's spelling: translate and retry both ways.
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timezoneId, out var ianaId) &&
            TryLookup(hostLookup, ianaId, out var viaIana))
            return viaIana;

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timezoneId, out var windowsId) &&
            TryLookup(hostLookup, windowsId, out var viaWindows))
            return viaWindows;

        return TimeZoneInfo.Utc;
    }

    private static bool TryLookup(Func<string, TimeZoneInfo> hostLookup, string id, out TimeZoneInfo resolved)
    {
        try
        {
            resolved = hostLookup(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            resolved = TimeZoneInfo.Utc;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            resolved = TimeZoneInfo.Utc;
            return false;
        }
    }
}

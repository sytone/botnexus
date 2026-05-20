namespace BotNexus.Cron.Actions;

/// <summary>
/// Shared timezone resolution helper used by heartbeat and other cron actions.
/// </summary>
internal static class TimeZoneHelper
{
    /// <summary>
    /// Resolves a timezone ID to a <see cref="TimeZoneInfo"/>, trying IANA and Windows IDs.
    /// Returns <see cref="TimeZoneInfo.Utc"/> when the ID is unrecognised or empty.
    /// </summary>
    internal static TimeZoneInfo Resolve(string? timezoneId)
    {
        if (string.IsNullOrWhiteSpace(timezoneId) ||
            timezoneId.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timezoneId, out var ianaId))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(ianaId); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timezoneId, out var windowsId))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(windowsId); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.Utc;
    }
}

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cron;

/// <summary>
/// Single definition of how a job's <c>timeoutSeconds</c> metadata value becomes an effective run
/// timeout (#2904).
/// </summary>
/// <remarks>
/// <para>
/// There used to be two near-identical copies of this - <c>CronScheduler.ResolveJobTimeout</c> and
/// <c>CommandCronAction.ResolveTimeout</c> - and they had already drifted: the scheduler accepted
/// <c>double</c> and <c>JsonElement</c>, the command action did not, so the same metadata value
/// resolved differently depending on which action fired. Keeping one seam is what makes AC5 ("the
/// sentinel is honoured across every accepted value shape") a property of the system rather than a
/// property of whichever site was edited last.
/// </para>
/// <para>
/// The <b>return contract</b> is the point of the change: <c>null</c> means <i>unlimited</i>, which
/// is distinct from "unset". Previously a non-positive value was silently discarded and replaced by
/// the default, so an operator had no way to express "this job legitimately runs long" short of a
/// large magic number. Now <c>0</c> is an explicit sentinel and a caller that receives <c>null</c>
/// must arm no <c>CancelAfter</c> at all - the run stays bounded by the ambient token (gateway
/// shutdown / explicit cancel) only.
/// </para>
/// <para>
/// A negative or unparseable value is still invalid, but is no longer silent: it warns naming the
/// job and the offending value, then falls back to the default. A silent fallback is what made the
/// original defect invisible in the logs.
/// </para>
/// </remarks>
internal static class CronTimeoutResolver
{
    /// <summary>Job metadata key carrying the per-job timeout override.</summary>
    internal const string MetadataKey = "timeoutSeconds";

    /// <summary>
    /// Explicit "no timeout" sentinel. Distinct from an absent key, which still means "use the
    /// default".
    /// </summary>
    internal const int UnlimitedSentinel = 0;

    /// <summary>
    /// Resolves the effective timeout for <paramref name="job"/>.
    /// </summary>
    /// <param name="job">Job whose metadata is consulted.</param>
    /// <param name="defaultTimeoutSeconds">
    /// Timeout used when the key is absent, or when the supplied value is invalid. Must be positive;
    /// callers own their own default.
    /// </param>
    /// <param name="logger">Optional logger used to report an invalid value (AC4).</param>
    /// <returns>
    /// <c>null</c> when the operator asked for an unlimited run; otherwise a positive number of
    /// seconds.
    /// </returns>
    internal static int? Resolve(CronJob job, int defaultTimeoutSeconds, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (job.Metadata is null
            || !job.Metadata.TryGetValue(MetadataKey, out var raw)
            || raw is null)
        {
            // Absent/unset: byte-for-byte the previous behaviour (AC3).
            return defaultTimeoutSeconds;
        }

        if (!TryCoerceSeconds(raw, out var seconds))
        {
            logger?.LogWarning(
                "Cron job '{JobId}' has an unparseable '{MetadataKey}' value {RawValue} ({RawType}); "
                + "falling back to the default of {DefaultTimeoutSeconds}s.",
                job.Id, MetadataKey, raw, raw.GetType().Name, defaultTimeoutSeconds);
            return defaultTimeoutSeconds;
        }

        if (seconds == UnlimitedSentinel)
        {
            // Explicit operator intent: run unbounded. The caller must not arm a CancelAfter.
            return null;
        }

        if (seconds < 0)
        {
            logger?.LogWarning(
                "Cron job '{JobId}' has a negative '{MetadataKey}' value {RawValue}; "
                + "falling back to the default of {DefaultTimeoutSeconds}s. "
                + "Use 0 to request an unlimited run.",
                job.Id, MetadataKey, seconds, defaultTimeoutSeconds);
            return defaultTimeoutSeconds;
        }

        return (int)Math.Min(seconds, int.MaxValue);
    }

    /// <summary>
    /// Normalises every metadata value shape the two call sites accepted between them into a single
    /// <see cref="long"/>, so the sentinel and the negative-value warning apply uniformly (AC5).
    /// </summary>
    private static bool TryCoerceSeconds(object raw, out long seconds)
    {
        switch (raw)
        {
            case int i:
                seconds = i;
                return true;
            case long l:
                seconds = l;
                return true;
            case double d:
                // Truncate toward zero, matching the previous (int)d cast. Guard the cast so a NaN
                // or out-of-range double is reported as unparseable rather than wrapping silently.
                if (double.IsNaN(d) || d > long.MaxValue || d < long.MinValue)
                    break;
                seconds = (long)d;
                return true;
            case JsonElement je:
                if (je.ValueKind == JsonValueKind.Number && je.TryGetInt64(out var jeNum))
                {
                    seconds = jeNum;
                    return true;
                }

                if (je.ValueKind == JsonValueKind.String
                    && long.TryParse(je.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var jeStr))
                {
                    seconds = jeStr;
                    return true;
                }

                break;
            case string s:
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    seconds = parsed;
                    return true;
                }

                break;
        }

        seconds = 0;
        return false;
    }
}

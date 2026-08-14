using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway;

/// <summary>
/// Decides which sessions the session-directory disk budget should evict (issue #2848).
/// </summary>
/// <remarks>
/// <para>
/// This is a pure function of (usage rows, options, in-flight predicate) so the eviction policy
/// can be tested exhaustively without a store, a timer, or a filesystem. The service owns the I/O;
/// the planner owns the decision.
/// </para>
/// <para>
/// Order is tiered, oldest-first within each tier: sealed sessions, then expired sessions, then
/// suspended, and active sessions are never evicted by the size path. Sealed transcripts are the
/// cheapest thing to lose - they are finished work no run can resume - so they go first.
/// </para>
/// </remarks>
public static class SessionDiskBudgetPlanner
{
    /// <summary>The outcome of planning one cleanup cycle's size pass.</summary>
    /// <param name="BudgetEnabled">False when the budget is disabled; nothing may be evicted.</param>
    /// <param name="TotalBytes">Total accounted bytes across the supplied usage rows.</param>
    /// <param name="MaxDiskBytes">The resolved budget, or 0 when disabled.</param>
    /// <param name="HighWaterBytes">The resolved reclaim target, or 0 when disabled.</param>
    /// <param name="OverBudget">True when <paramref name="TotalBytes"/> exceeds the budget.</param>
    /// <param name="Evictions">Sessions to delete, in eviction order. Always empty in Warn mode.</param>
    public sealed record Plan(
        bool BudgetEnabled,
        long TotalBytes,
        long MaxDiskBytes,
        long HighWaterBytes,
        bool OverBudget,
        IReadOnlyList<SessionDiskUsage> Evictions);

    private static readonly Plan Disabled =
        new(false, 0, 0, 0, false, Array.Empty<SessionDiskUsage>());

    /// <summary>
    /// Builds the eviction plan for one sweep.
    /// </summary>
    /// <param name="usages">Per-session disk accounting rows for a single agent's sessions.</param>
    /// <param name="options">The cleanup options carrying the budget configuration.</param>
    /// <param name="hasInFlightRun">
    /// Predicate returning true when the session has a live agent run. Such a session is NEVER
    /// evicted, even when it is the oldest and the budget is exceeded (#2848 AC5) - deleting it
    /// would pull the store out from under a running turn, the same failure #2395 fixed.
    /// </param>
    public static Plan BuildPlan(
        IReadOnlyList<SessionDiskUsage> usages,
        SessionCleanupOptions options,
        Func<string, bool> hasInFlightRun)
    {
        ArgumentNullException.ThrowIfNull(usages);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hasInFlightRun);

        // AC2: null / zero / negative disables the budget outright. Returning here - before any
        // total is even computed - is what makes it impossible for a zero budget to be read as
        // "everything is over budget" and wipe the store (openclaw#119422).
        var max = options.ResolveMaxDiskBytes();
        if (max is null)
            return Disabled;

        var maxBytes = max.Value;
        var highWater = options.ResolveHighWaterBytes(maxBytes);

        long total = 0;
        foreach (var usage in usages)
            total += usage.Bytes;

        var overBudget = total > maxBytes;
        if (!overBudget || options.DiskBudgetMode != SessionDiskBudgetMode.Enforce)
        {
            // AC3: Warn mode reports pressure and evicts nothing.
            return new Plan(true, total, maxBytes, highWater, overBudget, Array.Empty<SessionDiskUsage>());
        }

        var candidates = usages
            .Where(u => Tier(u.Status) is not null)
            .Where(u => !hasInFlightRun(u.SessionId))
            .OrderBy(u => Tier(u.Status)!.Value)
            .ThenBy(u => u.UpdatedAt)
            .ThenBy(u => u.SessionId, StringComparer.Ordinal)
            .ToList();

        var evictions = new List<SessionDiskUsage>();
        var remaining = total;
        foreach (var candidate in candidates)
        {
            // AC4: stop as soon as the footprint is at or below the high-water mark. Checking
            // BEFORE each eviction means we never delete one more session than the target needs.
            if (remaining <= highWater)
                break;

            evictions.Add(candidate);
            remaining -= candidate.Bytes;
        }

        return new Plan(true, total, maxBytes, highWater, true, evictions);
    }

    /// <summary>
    /// Eviction tier for a status, lower evicts first. <c>null</c> means never evicted by the
    /// size path: an Active session is live work, and the budget must not race the user for it.
    /// </summary>
    private static int? Tier(SessionStatus status) => status switch
    {
        SessionStatus.Sealed => 0,
        SessionStatus.Expired => 1,
        SessionStatus.Suspended => 2,
        _ => null,
    };
}

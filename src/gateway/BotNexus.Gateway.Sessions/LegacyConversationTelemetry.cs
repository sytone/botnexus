namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Identifies which call path activated <see cref="LegacyConversationResolver"/>.
/// </summary>
/// <remarks>
/// #2311 audit gate. The resolver services a completed one-time migration (#615) and is
/// slated for deletion; before it can be removed we must know whether anything still
/// reaches it in a live environment, and if so, <b>which</b> path. The distinction
/// matters: <see cref="StartupMigration"/> activity is the eager forward migration doing
/// its job and is expected to fall to zero once an environment has been swept, whereas
/// <see cref="LoadTimeBackfill"/> or <see cref="SaveTimeStamp"/> activity means unmigrated
/// data is still arriving and the shim is genuinely load-bearing.
/// </remarks>
public enum LegacyResolveReason
{
    /// <summary>Caller did not attribute itself. Counted in the total but not diagnosable.</summary>
    Unspecified = 0,

    /// <summary>The one-shot eager sweep run once per process at store initialisation.</summary>
    StartupMigration,

    /// <summary>A session was read from durable storage with no persisted conversation id.</summary>
    LoadTimeBackfill,

    /// <summary>A session reached the save path with an uninitialised conversation id.</summary>
    SaveTimeStamp
}

/// <summary>
/// Immutable point-in-time reading of the legacy-conversation activation counters.
/// </summary>
/// <remarks>
/// A default-valued snapshot represents "nothing has ever touched the shim", which is the
/// state that authorises deleting <see cref="LegacyConversationResolver"/> outright.
/// </remarks>
public readonly record struct LegacyConversationTelemetrySnapshot
{
    /// <summary>Total resolve calls across every call path.</summary>
    public long TotalResolves { get; init; }

    /// <summary>Resolve calls that actually minted a new legacy conversation.</summary>
    public long TotalCreates { get; init; }

    /// <summary>Bind calls that actually wrote an active-session pointer.</summary>
    public long TotalBinds { get; init; }

    /// <summary>Resolves attributed to the eager startup sweep.</summary>
    public long StartupMigrationResolves { get; init; }

    /// <summary>Resolves attributed to reading an unmigrated session from storage.</summary>
    public long LoadTimeBackfillResolves { get; init; }

    /// <summary>Resolves attributed to stamping an unset conversation id at save time.</summary>
    public long SaveTimeStampResolves { get; init; }

    /// <summary>Resolves from callers that did not attribute themselves.</summary>
    public long UnspecifiedResolves { get; init; }

    /// <summary>
    /// True when the shim was activated at least once. While this reads true in any
    /// environment, <see cref="LegacyConversationResolver"/> cannot be deleted.
    /// </summary>
    public bool HasActivity => TotalResolves > 0 || TotalCreates > 0 || TotalBinds > 0;
}

/// <summary>
/// Process-wide activation counters for <see cref="LegacyConversationResolver"/>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately static and allocation-free: the resolver runs on the session load and save
/// paths, so instrumentation must not add per-call allocation or require threading a new
/// dependency through three session stores that construct the resolver directly.
/// </para>
/// <para>
/// This type is intentionally short-lived. It exists only to answer the #2311 audit
/// question ("is anything still hitting the legacy shim?"). When the answer is confirmed
/// to be no, this file is deleted along with the resolver it measures - see
/// <c>docs/development/compat-shim-lifecycle.md</c>.
/// </para>
/// </remarks>
public static class LegacyConversationTelemetry
{
    private static long s_totalResolves;
    private static long s_totalCreates;
    private static long s_totalBinds;
    private static long s_startupMigrationResolves;
    private static long s_loadTimeBackfillResolves;
    private static long s_saveTimeStampResolves;
    private static long s_unspecifiedResolves;

    /// <summary>Records one resolve attributed to <paramref name="reason"/>.</summary>
    public static void RecordResolve(LegacyResolveReason reason)
    {
        Interlocked.Increment(ref s_totalResolves);

        switch (reason)
        {
            case LegacyResolveReason.StartupMigration:
                Interlocked.Increment(ref s_startupMigrationResolves);
                break;
            case LegacyResolveReason.LoadTimeBackfill:
                Interlocked.Increment(ref s_loadTimeBackfillResolves);
                break;
            case LegacyResolveReason.SaveTimeStamp:
                Interlocked.Increment(ref s_saveTimeStampResolves);
                break;
            default:
                Interlocked.Increment(ref s_unspecifiedResolves);
                break;
        }
    }

    /// <summary>Records that a resolve minted a brand-new legacy conversation.</summary>
    public static void RecordCreate() => Interlocked.Increment(ref s_totalCreates);

    /// <summary>Records that a bind actually wrote an active-session pointer.</summary>
    public static void RecordBind() => Interlocked.Increment(ref s_totalBinds);

    /// <summary>Reads all counters. Individual reads are atomic; the set is not a torn-free transaction.</summary>
    public static LegacyConversationTelemetrySnapshot Snapshot() => new()
    {
        TotalResolves = Interlocked.Read(ref s_totalResolves),
        TotalCreates = Interlocked.Read(ref s_totalCreates),
        TotalBinds = Interlocked.Read(ref s_totalBinds),
        StartupMigrationResolves = Interlocked.Read(ref s_startupMigrationResolves),
        LoadTimeBackfillResolves = Interlocked.Read(ref s_loadTimeBackfillResolves),
        SaveTimeStampResolves = Interlocked.Read(ref s_saveTimeStampResolves),
        UnspecifiedResolves = Interlocked.Read(ref s_unspecifiedResolves)
    };
}

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// An accumulator for <see cref="LegacyConversationTelemetry"/> activations that is scoped
/// to the async flow that created it, rather than to the process.
/// </summary>
/// <remarks>
/// <para>
/// #3227. <see cref="LegacyConversationTelemetry"/>'s counters are deliberately static:
/// the production audit question they answer ("has <b>anything</b> in this process reached
/// the shim?") is a process-wide question, and the session stores that raise the counters
/// construct the resolver directly with no DI seam to thread an instance through.
/// </para>
/// <para>
/// That makes the statics unusable as a <b>test</b> oracle. A test that snapshots them
/// before and after its own bind and asserts an exact delta is really asserting that no
/// other code in the process incremented the same counter in between - which xUnit's
/// parallel collections make false, non-deterministically. The fix is to give the
/// assertion a seam it actually controls: increments raised on a different async flow
/// cannot flow into this scope, so the delta measures only what the test itself caused.
/// </para>
/// <para>
/// Scoping was chosen over serialising the tests into a shared <c>[Collection]</c>.
/// Serialisation only holds while every writer to the counters is a test in that one
/// collection; it is silently defeated by any concurrent non-test writer, and it would
/// re-break the moment a new snapshotting test class forgot the attribute. Scoping makes
/// the interference structurally impossible instead of merely improbable.
/// </para>
/// <para>
/// Scopes nest: an increment is credited to the innermost active scope and to each of its
/// ancestors, so an outer scope still totals everything its inner scopes observed.
/// Disposal restores the previously ambient scope. The counters use
/// <see cref="Interlocked"/> so a scope shared across parallel work inside one test stays
/// accurate.
/// </para>
/// </remarks>
public sealed class LegacyConversationTelemetryScope : IDisposable
{
    private readonly LegacyConversationTelemetryScope? _parent;
    private bool _disposed;

    private long _totalResolves;
    private long _totalCreates;
    private long _totalBinds;
    private long _startupMigrationResolves;
    private long _loadTimeBackfillResolves;
    private long _saveTimeStampResolves;
    private long _unspecifiedResolves;

    internal LegacyConversationTelemetryScope()
    {
        _parent = LegacyConversationTelemetry.CurrentScope;
        LegacyConversationTelemetry.CurrentScope = this;
    }

    /// <summary>
    /// Reads the activations attributed to this scope. Because the scope starts empty and
    /// only ever accumulates this flow's activity, callers assert on this value directly -
    /// there is no before/after delta to compute, and therefore no window in which a
    /// sibling can interfere.
    /// </summary>
    public LegacyConversationTelemetrySnapshot Snapshot() => new()
    {
        TotalResolves = Interlocked.Read(ref _totalResolves),
        TotalCreates = Interlocked.Read(ref _totalCreates),
        TotalBinds = Interlocked.Read(ref _totalBinds),
        StartupMigrationResolves = Interlocked.Read(ref _startupMigrationResolves),
        LoadTimeBackfillResolves = Interlocked.Read(ref _loadTimeBackfillResolves),
        SaveTimeStampResolves = Interlocked.Read(ref _saveTimeStampResolves),
        UnspecifiedResolves = Interlocked.Read(ref _unspecifiedResolves)
    };

    internal void RecordResolve(LegacyResolveReason reason)
    {
        Interlocked.Increment(ref _totalResolves);

        switch (reason)
        {
            case LegacyResolveReason.StartupMigration:
                Interlocked.Increment(ref _startupMigrationResolves);
                break;
            case LegacyResolveReason.LoadTimeBackfill:
                Interlocked.Increment(ref _loadTimeBackfillResolves);
                break;
            case LegacyResolveReason.SaveTimeStamp:
                Interlocked.Increment(ref _saveTimeStampResolves);
                break;
            default:
                Interlocked.Increment(ref _unspecifiedResolves);
                break;
        }

        _parent?.RecordResolve(reason);
    }

    internal void RecordCreate()
    {
        Interlocked.Increment(ref _totalCreates);
        _parent?.RecordCreate();
    }

    internal void RecordBind()
    {
        Interlocked.Increment(ref _totalBinds);
        _parent?.RecordBind();
    }

    /// <summary>Restores the previously ambient scope. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        LegacyConversationTelemetry.CurrentScope = _parent;
    }
}

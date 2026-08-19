namespace BotNexus.Persistence.Seam.Tests.Harness;

/// <summary>
/// What happened when the stale writer attempted its write.
/// </summary>
public enum StaleWriteOutcome
{
    /// <summary>The stale write was accepted by the store.</summary>
    Accepted,

    /// <summary>The stale write was refused with a concurrency error — the desired behaviour.</summary>
    Rejected,
}

/// <summary>
/// The result of running a <see cref="LostUpdateScenario{TAggregate}"/>: what the stale write did,
/// the exception it was refused with (if any), and the aggregate as re-read from a FRESH store
/// instance afterwards.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
public sealed class LostUpdateResult<TAggregate>
{
    internal LostUpdateResult(StaleWriteOutcome outcome, Exception? rejection, TAggregate? committed)
    {
        Outcome = outcome;
        Rejection = rejection;
        Committed = committed;
    }

    /// <summary>Whether the stale write was accepted or refused.</summary>
    public StaleWriteOutcome Outcome { get; }

    /// <summary>The exception the stale write was refused with, or <c>null</c> when accepted.</summary>
    public Exception? Rejection { get; }

    /// <summary>
    /// The aggregate re-read from a fresh store instance after the interleaving. Read through a
    /// NEW store so an in-process cache cannot answer the verification query — the assertion has
    /// to see what is actually committed on disk, not what the writer's own store remembers.
    /// </summary>
    public TAggregate? Committed { get; }
}

/// <summary>
/// A reusable, deterministic lost-update contract test for a persistence seam (issue #2130).
/// </summary>
/// <remarks>
/// <para>
/// The shape it encodes is the one that produced the webhook conversation-pin regression:
/// </para>
/// <list type="number">
///   <item>a caller READS an aggregate, taking a detached snapshot;</item>
///   <item>an independent, narrower operation COMMITS a change to part of that aggregate;</item>
///   <item>the caller WRITES its snapshot back, carrying the pre-change value of the field the
///     narrow operation just wrote.</item>
/// </list>
/// <para>
/// Step 2 must be observed to happen strictly between steps 1 and 3, otherwise the test proves
/// nothing. The scenario enforces that ordering directly — it awaits the concurrent mutation to
/// completion before invoking the stale write — and offers <see cref="SeamGate"/> for tests that
/// need to interleave two genuinely parallel arms. No step is ordered by a sleep.
/// </para>
/// <para>
/// The scenario deliberately asserts NOTHING itself. It returns the committed state and lets the
/// test state the invariant, because "which field must survive" is the domain knowledge that
/// varies per seam, and a harness that guessed it would quietly weaken assertions.
/// </para>
/// </remarks>
/// <typeparam name="TAggregate">The aggregate type under test.</typeparam>
public sealed class LostUpdateScenario<TAggregate>
{
    private Func<Task<TAggregate?>>? _read;
    private Func<Task>? _concurrentMutation;
    private Func<TAggregate, Task>? _staleWrite;
    private Func<Task<TAggregate?>>? _reload;

    /// <summary>Step 1: how the caller takes its snapshot of the aggregate.</summary>
    public LostUpdateScenario<TAggregate> ReadSnapshot(Func<Task<TAggregate?>> read)
    {
        _read = read;
        return this;
    }

    /// <summary>
    /// Step 2: the independent narrow mutation that commits AFTER the snapshot is taken. Awaited
    /// to completion before the stale write runs, so "the mutation had already committed" is a
    /// fact of the test rather than a hope about scheduling.
    /// </summary>
    public LostUpdateScenario<TAggregate> ThenConcurrently(Func<Task> mutation)
    {
        _concurrentMutation = mutation;
        return this;
    }

    /// <summary>
    /// Step 3: the caller's write of its now-stale snapshot. Any exception is captured rather than
    /// thrown so the test can assert on the refusal itself.
    /// </summary>
    public LostUpdateScenario<TAggregate> ThenStaleWrite(Func<TAggregate, Task> staleWrite)
    {
        _staleWrite = staleWrite;
        return this;
    }

    /// <summary>
    /// How to re-read committed state for verification. MUST use a store instance distinct from
    /// the one that performed the writes so no in-memory cache can satisfy the read.
    /// </summary>
    public LostUpdateScenario<TAggregate> VerifyBy(Func<Task<TAggregate?>> reload)
    {
        _reload = reload;
        return this;
    }

    /// <summary>Runs the interleaving and returns what was committed.</summary>
    /// <exception cref="InvalidOperationException">A required step was not configured.</exception>
    public async Task<LostUpdateResult<TAggregate>> RunAsync()
    {
        var read = _read ?? throw new InvalidOperationException("ReadSnapshot was not configured.");
        var mutation = _concurrentMutation ?? throw new InvalidOperationException("ThenConcurrently was not configured.");
        var staleWrite = _staleWrite ?? throw new InvalidOperationException("ThenStaleWrite was not configured.");
        var reload = _reload ?? throw new InvalidOperationException("VerifyBy was not configured.");

        var snapshot = await read().ConfigureAwait(false);
        if (snapshot is null)
        {
            throw new InvalidOperationException(
                "The seam scenario read a null aggregate. The fixture did not create the row the " +
                "scenario is about, so any subsequent assertion would pass vacuously.");
        }

        await mutation().ConfigureAwait(false);

        StaleWriteOutcome outcome;
        Exception? rejection = null;
        try
        {
            await staleWrite(snapshot).ConfigureAwait(false);
            outcome = StaleWriteOutcome.Accepted;
        }
        catch (Exception ex)
        {
            outcome = StaleWriteOutcome.Rejected;
            rejection = ex;
        }

        var committed = await reload().ConfigureAwait(false);
        return new LostUpdateResult<TAggregate>(outcome, rejection, committed);
    }
}

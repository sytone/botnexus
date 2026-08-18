using System.Runtime.CompilerServices;

namespace BotNexus.Persistence.Seam.Tests.Harness;

/// <summary>
/// A single-shot, deterministic rendezvous point used to order the steps of two concurrent
/// arms in a persistence seam test.
/// </summary>
/// <remarks>
/// <para>
/// Seam tests exist to prove behaviour under a SPECIFIC interleaving — "the reader read, THEN
/// the narrow mutation committed, THEN the reader wrote". A <c>Task.Delay</c> or a
/// <c>Thread.Sleep</c> can only make that ordering <em>likely</em>; on a loaded CI agent it
/// silently degrades into whatever ordering the scheduler picked, which turns a lost-update
/// assertion into a coin flip. This gate makes the ordering a property of the test rather than
/// of the machine: an arm that waits does not proceed until the other arm has signalled, full
/// stop.
/// </para>
/// <para>
/// The timeout is NOT a synchronisation mechanism — it is a deadlock detector. If it fires, the
/// test has been mis-wired (an arm is waiting on a gate nobody signals) and the failure message
/// names the gate so the mis-wiring is obvious rather than presenting as a hang.
/// </para>
/// </remarks>
public sealed class SeamGate
{
    private readonly TaskCompletionSource _source =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="name">
    /// Human-readable name of the point in the interleaving this gate represents, surfaced in
    /// deadlock diagnostics.
    /// </param>
    public SeamGate([CallerMemberName] string name = "gate")
    {
        Name = name;
    }

    /// <summary>The name of this gate, used in deadlock diagnostics.</summary>
    public string Name { get; }

    /// <summary>Whether <see cref="Open"/> has already been called.</summary>
    public bool IsOpen => _source.Task.IsCompleted;

    /// <summary>
    /// Opens the gate, releasing every current and future waiter. Idempotent: opening an already
    /// open gate is a no-op rather than an error, so an arm that runs twice under a
    /// <c>[Theory]</c> does not need defensive bookkeeping.
    /// </summary>
    public void Open() => _source.TrySetResult();

    /// <summary>
    /// Waits until the gate is opened by another arm. Returns immediately when already open.
    /// </summary>
    /// <param name="timeout">
    /// Deadlock budget. Exceeding it is always a test-wiring bug, never a legitimate slow path,
    /// because nothing in a seam test should take seconds.
    /// </param>
    public async Task WaitAsync(TimeSpan? timeout = null)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(30);
        var completed = await Task.WhenAny(_source.Task, Task.Delay(budget)).ConfigureAwait(false);
        if (completed != _source.Task)
        {
            throw new SeamDeadlockException(
                $"Seam gate '{Name}' was never opened within {budget.TotalSeconds:0.#}s. " +
                "The interleaving is mis-wired: some arm is waiting on a gate no arm opens. " +
                "This is a test-authoring bug, not a timing flake.");
        }
    }

    /// <summary>Opens this gate and then waits for another — the common "hand off" step.</summary>
    public Task OpenThenWaitAsync(SeamGate other, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(other);
        Open();
        return other.WaitAsync(timeout);
    }
}

/// <summary>
/// Raised when a <see cref="SeamGate"/> is never opened, i.e. the test's interleaving cannot
/// complete. Distinct from an assertion failure so a mis-wired test is never mistaken for a
/// genuine persistence defect.
/// </summary>
public sealed class SeamDeadlockException : Exception
{
    /// <summary>Creates the exception with the supplied diagnostic message.</summary>
    public SeamDeadlockException(string message) : base(message)
    {
    }
}

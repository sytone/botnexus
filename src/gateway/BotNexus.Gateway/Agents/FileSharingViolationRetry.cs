namespace BotNexus.Gateway.Agents;

/// <summary>
/// Bounded retry for file operations that transiently fail because another process holds an
/// incompatible handle on the target file (issue #2909).
/// </summary>
/// <remarks>
/// The durable memory-note append is a write we must not silently lose. Two writers colliding on the
/// same daily note produced <c>IOException: The process cannot access the file ... because it is being
/// used by another process</c>, which propagated straight out of the tool call and discarded the note.
/// A sharing violation is transient by nature — the competing handle is released microseconds later —
/// so this mirrors the <c>SqliteRetryHelper</c> precedent already in the repo: retry a small, bounded
/// number of times with jittered backoff, and only surface a failure once every attempt is exhausted.
/// Jitter matters because the colliding writers are typically two agents woken by the same event; a
/// fixed backoff would keep them in lockstep and re-collide on every attempt.
/// </remarks>
public static class FileSharingViolationRetry
{
    /// <summary>Default number of attempts, including the initial one.</summary>
    public const int DefaultMaxAttempts = 5;

    /// <summary>Base backoff in milliseconds; the actual delay is this value jittered.</summary>
    public const int DefaultBaseDelayMs = 50;

    // Win32 error codes surfaced through IOException.HResult for contended files.
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    /// <summary>
    /// Executes <paramref name="operation"/>, retrying while it fails with a sharing/lock violation.
    /// </summary>
    /// <param name="operation">The file operation to attempt. Receives the 1-based attempt number.</param>
    /// <param name="description">Human-readable description of the operation, used in the failure message.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="maxAttempts">Total attempts, including the first.</param>
    /// <param name="baseDelayMs">Base backoff in milliseconds between attempts.</param>
    /// <param name="delay">Delay hook; injectable so tests are deterministic and do not sleep.</param>
    /// <exception cref="IOException">
    /// Thrown when every attempt failed with a sharing violation. The message states that the retries
    /// happened so an operator can distinguish genuine contention from a one-shot failure.
    /// </exception>
    public static async Task ExecuteAsync(
        Func<int, CancellationToken, Task> operation,
        string description,
        CancellationToken ct = default,
        int maxAttempts = DefaultMaxAttempts,
        int baseDelayMs = DefaultBaseDelayMs,
        Func<int, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        delay ??= static (ms, token) => Task.Delay(ms, token);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await operation(attempt, ct).ConfigureAwait(false);
                return;
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                if (attempt >= maxAttempts)
                {
                    throw new IOException(
                        $"{description} failed after {maxAttempts} attempts because the file remained locked by another process. " +
                        "The write was not applied.",
                        ex);
                }

                await delay(NextDelayMs(baseDelayMs, attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Determines whether an <see cref="IOException"/> represents a transient sharing or lock violation.
    /// </summary>
    /// <remarks>
    /// Only the low 16 bits of the HResult carry the Win32 code; the facility bits differ depending on
    /// whether the exception was raised from a raw code or an HRESULT-wrapped one, so both shapes are
    /// accepted. Anything else (disk full, path not found, access denied) is NOT transient and must
    /// surface immediately rather than being retried into a delay.
    /// </remarks>
    public static bool IsSharingViolation(IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var code = exception.HResult & 0xFFFF;
        return code is ErrorSharingViolation or ErrorLockViolation;
    }

    /// <summary>
    /// Computes the jittered backoff for an attempt: the base delay plus up to 100% random jitter.
    /// </summary>
    private static int NextDelayMs(int baseDelayMs, int attempt)
    {
        var scaled = baseDelayMs * attempt;
        return scaled + Random.Shared.Next(0, Math.Max(1, baseDelayMs));
    }
}

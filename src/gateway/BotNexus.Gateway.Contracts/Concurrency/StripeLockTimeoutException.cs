namespace BotNexus.Gateway.Abstractions.Concurrency;

/// <summary>
/// Thrown when a bounded <see cref="StripedAsyncLock.AcquireAsync{TKey}(TKey, TimeSpan, CancellationToken)"/>
/// gives up because another caller still holds the stripe (#3517).
/// </summary>
/// <remarks>
/// <para>
/// This exists so lock contention is DIAGNOSABLE at the point it is logged. #3517 produced 154
/// identical <c>TaskCanceledException</c> stacks from a <c>SemaphoreSlim.WaitAsync</c> reached with
/// <see cref="CancellationToken.None"/> — a shape that reads as "somebody cancelled me" when in
/// fact nobody had, and which cost the whole diagnosis. A named type carrying the key and the
/// bound turns that into a one-line read.
/// </para>
/// <para>
/// It deliberately derives from <see cref="TimeoutException"/> rather than
/// <see cref="OperationCanceledException"/>: a caller that catches cancellation is handling
/// "my work was called off", which is exactly what this is not.
/// </para>
/// </remarks>
public sealed class StripeLockTimeoutException : TimeoutException
{
    /// <summary>Creates the exception for <paramref name="key"/> and the elapsed <paramref name="timeout"/>.</summary>
    public StripeLockTimeoutException(string? key, TimeSpan timeout)
        : base($"Timed out after {timeout.TotalSeconds:0.###}s waiting for the lock stripe of key '{key}'; it is held by another caller that has not released it.")
    {
        Key = key;
        Timeout = timeout;
    }

    /// <summary>The key whose stripe could not be acquired.</summary>
    public string? Key { get; }

    /// <summary>The bound that elapsed.</summary>
    public TimeSpan Timeout { get; }
}

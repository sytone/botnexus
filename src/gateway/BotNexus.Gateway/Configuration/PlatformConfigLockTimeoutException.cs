namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Thrown when a configuration write could not acquire the cross-process advisory lock guarding
/// <c>config.json</c> within the allowed time (issue #2134).
/// </summary>
/// <remarks>
/// <para>
/// This is the cross-<em>process</em> sibling of <see cref="PlatformConfigConcurrencyException"/>:
/// that one reports "the document changed underneath your snapshot", this one reports "another
/// process is inside the critical section right now and did not leave in time". Both exist for the
/// same reason - a lost configuration write must never be silent.
/// </para>
/// <para>
/// The alternative on timeout would be to proceed without the lock, which is precisely the
/// interleaved read-modify-write that loses one writer's change. Failing loudly is the outcome the
/// #2134 acceptance criterion explicitly permits ("...or one receives an explicit concurrency
/// conflict"). Nothing is written and the file on disk is left untouched, so the caller may safely
/// retry.
/// </para>
/// </remarks>
public sealed class PlatformConfigLockTimeoutException : InvalidOperationException
{
    /// <summary>Gets the configuration file whose write could not be serialised.</summary>
    public string ConfigPath { get; }

    /// <summary>Gets the timeout, in milliseconds, that elapsed while waiting for the lock.</summary>
    public int TimeoutMilliseconds { get; }

    /// <summary>
    /// Initialises a new instance of the <see cref="PlatformConfigLockTimeoutException"/> class.
    /// </summary>
    /// <param name="configPath">The configuration file whose write could not be serialised.</param>
    /// <param name="timeoutMilliseconds">The elapsed acquisition timeout.</param>
    /// <param name="innerException">The last file-sharing failure observed.</param>
    public PlatformConfigLockTimeoutException(string configPath, int timeoutMilliseconds, Exception? innerException = null)
        : base(
            $"Concurrency conflict: configuration '{configPath}' is locked by another BotNexus process "
            + $"and the lock could not be acquired within {timeoutMilliseconds}ms. Nothing was written. Retry the command.",
            innerException)
    {
        ConfigPath = configPath;
        TimeoutMilliseconds = timeoutMilliseconds;
    }
}

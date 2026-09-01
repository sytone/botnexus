using System.IO.Abstractions;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// An OS-level advisory lock over the configuration document, held for the duration of a
/// read-modify-write critical section.
/// </summary>
/// <remarks>
/// <para>
/// Issue #2134 (residual). <see cref="PlatformConfigWriter"/> serialises writers with a
/// <c>static SemaphoreSlim</c>. A static semaphore only ever coordinates threads inside <em>one</em>
/// process, and the CLI and the gateway are separate OS processes that both write
/// <c>~/.botnexus/config.json</c>. Two concurrent processes could therefore interleave
/// read-modify-write and silently drop one side's change - reproduced directly: two
/// <c>botnexus config set</c> processes on disjoint keys both reported success while only one key
/// reached disk.
/// </para>
/// <para>
/// The whole-document compare-and-swap already on main does <em>not</em> close this: it is opt-in
/// (the caller must pass <c>expectedRevision</c>) and only applies to
/// <see cref="PlatformConfigWriter.UpdatePlatformConfigAsync"/>. The CLI mutation path uses
/// <c>MutateValidatedAsync</c>, which never supplied a revision, so nothing constrained the second
/// process.
/// </para>
/// <para><b>Mechanism.</b> A sidecar file <c>config.json.lock</c> is opened with
/// <see cref="FileShare.None"/>. Exclusive-open semantics are honoured cross-process on every
/// platform .NET targets, unlike a named <see cref="Mutex"/>, which is process-local on Linux and
/// would therefore be useless in CI. The sidecar - not <c>config.json</c> itself - is locked so the
/// writer stays free to swap the config file atomically via <c>File.Replace</c>/<c>Move</c> while
/// holding the lock.
/// </para>
/// <para><b>Lock ordering (deadlock argument).</b> Acquisition is always
/// <c>SemaphoreSlim -&gt; file lock</c>, never the reverse, at every call site. With a single
/// consistent global order and no other lock in the critical section, a cycle cannot form. The
/// file lock is additionally bounded by a timeout, so even a foreign process holding the sidecar
/// forever degrades to a loud failure rather than a hang.
/// </para>
/// <para><b>Fail-safe.</b> If the lock cannot be taken within the timeout the writer throws
/// <see cref="PlatformConfigLockTimeoutException"/>. Proceeding without the lock would reinstate
/// the silent lost update, which is the defect; an explicit conflict is an outcome the acceptance
/// criterion permits.
/// </para>
/// </remarks>
internal sealed class CrossProcessConfigLock : IDisposable
{
    /// <summary>
    /// Environment override for the acquisition timeout, in milliseconds. Primarily a test seam so
    /// a blocked write fails fast instead of waiting out the production budget.
    /// </summary>
    public const string TimeoutEnvironmentVariable = "BOTNEXUS_CONFIG_LOCK_TIMEOUT_MS";

    private const int DefaultTimeoutMs = 10_000;

    private readonly Stream? _stream;

    private CrossProcessConfigLock(Stream? stream) => _stream = stream;

    /// <summary>
    /// Acquires the cross-process lock guarding <paramref name="configPath"/>, retrying with
    /// bounded backoff until the timeout elapses.
    /// </summary>
    /// <param name="timeProvider">
    /// Clock source for the acquisition bound. Defaults to <see cref="TimeProvider.System"/>; only the
    /// MONOTONIC surface (<see cref="TimeProvider.GetTimestamp"/>) is consulted, never
    /// <see cref="TimeProvider.GetUtcNow"/>. Exposed as a test seam so a regression test can step the
    /// wall clock independently of elapsed time (#3738).
    /// </param>
    /// <param name="timeoutMsOverride">
    /// Acquisition budget in milliseconds, bypassing <see cref="TimeoutEnvironmentVariable"/>. A test
    /// seam: the environment variable is process-global, so a test that mutated it would race every
    /// other test in the same assembly.
    /// </param>
    /// <remarks>
    /// <para><b>#3738 - the bound is measured monotonically.</b> This previously computed an absolute
    /// <c>DateTime.UtcNow.AddMilliseconds(timeoutMs)</c> instant and compared <c>UtcNow</c> against it.
    /// The wall clock is not monotonic: an NTP correction, a VM resume, or a container host time sync
    /// can step it. A BACKWARDS step pushes the deadline further away in wall-clock terms, so a
    /// 10-second bounded acquire becomes an unbounded one - the loop polls forever and the config write
    /// path hangs. A FORWARDS step expires the wait early and raises
    /// <see cref="PlatformConfigLockTimeoutException"/> for a lock that was never contended for the
    /// declared duration. Elapsed time is now read from the monotonic timestamp source, which no clock
    /// adjustment can move. This matches the already-correct sibling drain in
    /// <c>SupervisorSessionRunDrain</c>.</para>
    /// </remarks>
    /// <exception cref="PlatformConfigLockTimeoutException">The lock was still held at timeout.</exception>
    public static async Task<CrossProcessConfigLock> AcquireAsync(
        string configPath,
        IFileSystem fileSystem,
        CancellationToken ct,
        TimeProvider? timeProvider = null,
        int? timeoutMsOverride = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var lockPath = ResolveLockPath(configPath, fileSystem);
        var timeoutMs = timeoutMsOverride ?? ResolveTimeoutMs();

        var directory = fileSystem.Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrWhiteSpace(directory))
            fileSystem.Directory.CreateDirectory(directory);

        var startedAt = clock.GetTimestamp();
        var timeout = TimeSpan.FromMilliseconds(timeoutMs);
        var delayMs = 5;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var stream = fileSystem.FileStream.New(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new CrossProcessConfigLock(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (clock.GetElapsedTime(startedAt) >= timeout)
                    throw new PlatformConfigLockTimeoutException(configPath, timeoutMs, ex);
            }

            await Task.Delay(delayMs, ct);
            delayMs = Math.Min(delayMs * 2, 100);
        }
    }

    /// <summary>
    /// Resolves the sidecar lock path for <paramref name="configPath"/>.
    /// </summary>
    /// <remarks>
    /// The sidecar lives in a dedicated <c>locks/</c> subdirectory rather than beside
    /// <c>config.json</c>. The config directory has a pinned contract - it holds the config document
    /// and nothing else, asserted by the ConfigDiskE2E durability suite - and a lock file that
    /// outlives every write is indistinguishable from crash residue to those tests and to a human
    /// inspecting <c>~/.botnexus</c>. Keeping it on the same volume (rather than in %TEMP%)
    /// preserves the exclusive-open semantics the lock depends on and scopes the lock to the same
    /// home directory that owns the config.
    /// </remarks>
    internal static string ResolveLockPath(string configPath, IFileSystem fileSystem)
    {
        var directory = fileSystem.Path.GetDirectoryName(configPath);
        var fileName = fileSystem.Path.GetFileName(configPath) + ".lock";
        return string.IsNullOrWhiteSpace(directory)
            ? fileSystem.Path.Combine("locks", fileName)
            : fileSystem.Path.Combine(directory, "locks", fileName);
    }

    private static int ResolveTimeoutMs()
    {
        var raw = Environment.GetEnvironmentVariable(TimeoutEnvironmentVariable);
        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : DefaultTimeoutMs;
    }

    public void Dispose() => _stream?.Dispose();
}

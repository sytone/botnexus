using System.IO.Abstractions;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Skills;

/// <summary>Publishes complete extension-owned seeds without replacing operator content.</summary>
internal static class AtomicSeedFile
{
    internal static bool EnsureSeed(
        IFileSystem fs,
        string directory,
        string destination,
        Func<string> readContent,
        ILogger? logger,
        TimeProvider timeProvider,
        Action<TimeSpan> delay,
        SharingRetry? retry = null,
        Func<bool>? shouldSkip = null)
    {
        if (fs.File.Exists(destination))
            return false;

        retry ??= new SharingRetry(timeProvider, delay);
        retry.Execute(() => fs.Directory.CreateDirectory(directory));
        var temporary = fs.Path.Combine(directory, $".{fs.Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var content = readContent();
            retry.Execute(() => fs.File.WriteAllText(temporary, content));
            // Recheck skill discovery after preparation: a caller that created the global
            // directory cannot assume it is still empty. Publication arbitrates this file only.
            if (shouldSkip?.Invoke() == true)
                return false;
            try
            {
                retry.Execute(() => PublishCompletedFile(fs, temporary, destination));
            }
            catch (IOException error) when (IsCreateCollision(error) && fs.File.Exists(destination))
            {
                return false;
            }
            return true;
        }
        finally
        {
            try
            {
                new SharingRetry(timeProvider, delay).Execute(() => fs.File.Delete(temporary));
            }
            catch (Exception error)
            {
                // Cleanup owns only our sibling and must never replace the original failure.
                if (logger is not null)
                    logger.LogWarning(error, "Skills: could not remove owned seed temporary file '{TemporaryPath}'.", temporary);
                else
                    Trace.TraceWarning("Skills: could not remove owned seed temporary file '{0}': {1}", temporary, error);
            }
        }
    }

    internal static void PublishCompletedFile(IFileSystem fs, string source, string destination)
    {
        // Injected filesystem implementations own the no-replace Move contract (including mocks).
        // A real FileSystem on Unix must never use .NET's check-then-rename Move implementation.
        if (OperatingSystem.IsWindows() || fs.GetType() != typeof(FileSystem))
        {
            fs.File.Move(source, destination, overwrite: false);
            return;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
            throw new PlatformNotSupportedException(
                $"Atomic seed publication from '{source}' to '{destination}' requires Windows or Unix libc link support.");

        LinkCompletedFile(source, destination, (from, to) =>
            NativeMethods.Link(from, to) == 0 ? 0 : Marshal.GetLastPInvokeError());
    }

    // The narrowly scoped delegate returns errno (zero on success), allowing deterministic native
    // failures without substituting the producer or consumer. Never fall back to copy or rename.
    internal static void LinkCompletedFile(string source, string destination, Func<string, string, int> link)
    {
        var errno = link(source, destination);
        if (errno == 0)
            return;

        var nativeError = new Win32Exception(errno);
        var error = new IOException(
            $"Atomic seed publication link('{source}', '{destination}') failed (errno {errno}: {nativeError.Message}). "
            + "Use a writable filesystem supporting hard links; no copy or rename fallback is permitted.", nativeError)
        {
            HResult = errno
        };
        throw error;
    }

    private static class NativeMethods
    {
        // POSIX link atomically creates a new directory entry and fails with EEXIST rather than
        // replacing any existing entry, including a symlink. The completed source stays until finally.
        [DllImport("libc", EntryPoint = "link", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Link(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
    }

    private static bool IsCreateCollision(IOException error) =>
        error.HResult is unchecked((int)0x80070050) or unchecked((int)0x800700B7)
        // .NET on Unix exposes the raw EEXIST errno rather than an HRESULT.
        || (!OperatingSystem.IsWindows() && error.HResult == 17);

    private static bool IsSharingOrLockFailure(IOException error) =>
        error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)
        // Unix file sharing uses EWOULDBLOCK (EAGAIN): Linux 11, macOS/FreeBSD 35.
        || (OperatingSystem.IsLinux() && error.HResult == 11)
        || ((OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()) && error.HResult == 35);

    internal sealed class SharingRetry(TimeProvider timeProvider, Action<TimeSpan> delay)
    {
        private const int MaximumFailures = 8;
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);
        private readonly long _started = timeProvider.GetTimestamp();
        private int _failures;

        public void Execute(Action operation)
        {
            while (true)
            {
                try
                {
                    operation();
                    return;
                }
                catch (IOException error) when (IsSharingOrLockFailure(error))
                {
                    if (++_failures >= MaximumFailures)
                        throw;
                    var remaining = Deadline - timeProvider.GetElapsedTime(_started);
                    if (remaining <= TimeSpan.Zero)
                        throw;
                    delay(remaining < RetryDelay ? remaining : RetryDelay);
                    // A delayed wakeup must not launch another attempt past the deadline.
                    if (timeProvider.GetElapsedTime(_started) >= Deadline)
                        throw;
                }
            }
        }
    }
}

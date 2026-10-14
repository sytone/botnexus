using System.IO.Abstractions;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Skills;

/// <summary>Installs Skills-extension-owned prompt templates into the shared prompt catalogue.</summary>
public static class SkillsPromptSeeder
{
    internal const string OptimizationPromptFileName = "optimize-shared-skills.prompt.md";
    private const string OptimizationPromptResourceName =
        "BotNexus.Extensions.Skills.Resources.Prompts.optimize-shared-skills.prompt.md";

    /// <summary>
    /// Copies the bundled shared-skill optimization prompt when it is absent. Existing operator
    /// content is never overwritten.
    /// </summary>
    public static void EnsureSharedPromptSeed(
        string? sharedPromptsDir,
        IFileSystem? fileSystem = null,
        ILogger? logger = null)
        => EnsureSharedPromptSeed(sharedPromptsDir, fileSystem ?? new FileSystem(), logger, TimeProvider.System, Thread.Sleep);

    // A clock plus delay seam keeps deadline/attempt-limit tests independent of wall-clock sleeps.
    internal static void EnsureSharedPromptSeed(
        string? sharedPromptsDir,
        IFileSystem fs,
        ILogger? logger,
        TimeProvider timeProvider,
        Action<TimeSpan> delay)
    {
        if (string.IsNullOrWhiteSpace(sharedPromptsDir))
            return;

        var destinationPath = fs.Path.Combine(sharedPromptsDir, OptimizationPromptFileName);
        if (fs.File.Exists(destinationPath))
            return;

        var retry = new SharingRetry(timeProvider, delay);
        retry.Execute(() => fs.Directory.CreateDirectory(sharedPromptsDir));
        // A unique sibling stays on the same filesystem. WriteAllText closes it before publication;
        // only atomic no-replace publication arbitrates callers, not a process-local lock.
        var temporaryPath = fs.Path.Combine(sharedPromptsDir, $".{OptimizationPromptFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var content = ReadBundledPrompt();
            retry.Execute(() => fs.File.WriteAllText(temporaryPath, content));
            try
            {
                retry.Execute(() => PublishCompletedFile(fs, temporaryPath, destinationPath));
            }
            catch (IOException error) when (IsCreateCollision(error) && fs.File.Exists(destinationPath))
            {
                // Another seeder or operator won publication. Unrelated I/O failures must still
                // propagate even when the destination happens to exist.
                return;
            }

            logger?.LogInformation(
                "Skills: installed shared prompt template at '{PromptPath}'.",
                destinationPath);
        }
        finally
        {
            try
            {
                // Unix link leaves the owned sibling for cleanup; Windows Move already removed it.
                // Retry only sharing/lock failures,
                // using a separate bounded cleanup budget even when publication exhausted its own.
                new SharingRetry(timeProvider, delay).Execute(() => fs.File.Delete(temporaryPath));
            }
            catch (Exception error)
            {
                // Cleanup must never replace the original startup failure or undo a winning seed.
                if (logger is not null)
                    logger.LogWarning(error, "Skills: could not remove owned prompt temporary file '{TemporaryPath}'.", temporaryPath);
                else
                    Trace.TraceWarning("Skills: could not remove owned prompt temporary file '{0}': {1}", temporaryPath, error);
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
                $"Atomic prompt publication from '{source}' to '{destination}' requires Windows or Unix libc link support.");

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
            $"Atomic prompt publication link('{source}', '{destination}') failed (errno {errno}: {nativeError.Message}). "
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

    private sealed class SharingRetry(TimeProvider timeProvider, Action<TimeSpan> delay)
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

    private static string ReadBundledPrompt()
    {
        var assembly = typeof(SkillsPromptSeeder).Assembly;
        using var stream = assembly.GetManifestResourceStream(OptimizationPromptResourceName)
            ?? throw new InvalidOperationException(
                $"Bundled prompt resource '{OptimizationPromptResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

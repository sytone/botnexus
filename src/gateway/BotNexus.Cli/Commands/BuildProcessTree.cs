using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BotNexus.Cli.Commands;

/// <summary>
/// Retains descendant observers before a build root is killed. A successful tree-kill request
/// and root exit alone do not establish exit of the processes that held inherited build pipes.
/// </summary>
internal static class BuildProcessTree
{
    internal static async Task TerminateAsync(Process root, CancellationToken cancellationToken)
    {
        var descendants = new List<Process>();
        try
        {
            // No PID reattachment after the kill. Capture handles and exact start identities
            // while ancestry is still available; never kill an enumerated PID directly.
            CaptureDescendants(root, descendants, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (root.HasExited)
                throw new IOException("Build root exited before tree capture completed; descendant cleanup could not be confirmed.");
            root.Kill(entireProcessTree: true);
            await Task.WhenAll(descendants.Append(root).Select(process => process.WaitForExitAsync(cancellationToken)));
            foreach (var process in descendants)
            {
                process.Refresh();
                if (!process.HasExited)
                    throw new IOException("Owned build descendant exit could not be confirmed.");
            }
        }
        finally
        {
            foreach (var process in descendants)
                process.Dispose();
        }
    }

    private static void CaptureDescendants(Process root, List<Process> descendants, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            var identity = ReadLinuxIdentity(root.Id);
            CaptureLinuxChildren(root.Id, identity.Start, descendants, new HashSet<int> { root.Id }, cancellationToken);
            if (ReadLinuxIdentity(root.Id) != identity)
                throw new IOException("Build root identity changed during descendant capture.");
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            CaptureWindowsChildren(root, descendants, cancellationToken);
            return;
        }
        throw new PlatformNotSupportedException("Build descendant exit confirmation requires Windows or Linux.");
    }

    private static void CaptureLinuxChildren(int parent, ulong parentStart, List<Process> descendants, HashSet<int> seen, CancellationToken cancellationToken)
    {
        // Linux assigns children to the creating thread. Read every thread's child list,
        // not just the main thread, and never scan or signal unrelated machine processes.
        foreach (var task in Directory.EnumerateDirectories($"/proc/{parent}/task"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string children;
            try { children = File.ReadAllText(Path.Combine(task, "children")); }
            catch (DirectoryNotFoundException) { continue; } // A thread ended during capture.
            catch (FileNotFoundException) { continue; }
            foreach (var value in children.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pid = int.Parse(value, CultureInfo.InvariantCulture);
                if (!seen.Add(pid))
                    continue;
                Process? process = null;
                try
                {
                    var before = ReadLinuxIdentity(pid);
                    if (before.Parent != parent || before.Start < parentStart)
                        throw new IOException("Build descendant ancestry changed during capture.");
                    process = Process.GetProcessById(pid);
                    _ = process.SafeHandle;
                    if (ReadLinuxIdentity(pid) != before)
                        throw new IOException("Build descendant identity changed during capture.");
                    descendants.Add(process);
                    process = null;
                    CaptureLinuxChildren(pid, before.Start, descendants, seen, cancellationToken);
                }
                catch (DirectoryNotFoundException) { } // Already gone before any kill request.
                catch (FileNotFoundException) { }
                catch (ArgumentException) when (!Directory.Exists($"/proc/{pid}")) { }
                finally { process?.Dispose(); }
            }
        }
    }

    private static (int Parent, ulong Start) ReadLinuxIdentity(int pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        var close = stat.LastIndexOf(')');
        if (close < 0)
            throw new IOException("Cannot read owned build process identity.");
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19)
            throw new IOException("Incomplete owned build process identity.");
        return (int.Parse(fields[1], CultureInfo.InvariantCulture), ulong.Parse(fields[19], CultureInfo.InvariantCulture));
    }

    private static void CaptureWindowsChildren(Process root, List<Process> descendants, CancellationToken cancellationToken)
    {
        // Toolhelp supplies ancestry only. Open and retain each process handle, then check
        // its start time against the retained parent to reject recycled snapshot PIDs.
        var snapshotStartedUtc = DateTime.UtcNow;
        using var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snapshot.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var children = new Dictionary<int, List<int>>();
        if (!Process32First(snapshot, ref entry))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = checked((int)entry.ParentProcessId);
            if (!children.TryGetValue(parent, out var ids))
                children[parent] = ids = [];
            ids.Add(checked((int)entry.ProcessId));
        } while (Process32Next(snapshot, ref entry));
        var error = Marshal.GetLastWin32Error();
        if (error != 18) // ERROR_NO_MORE_FILES
            throw new Win32Exception(error);
        var pending = new Queue<Process>();
        pending.Enqueue(root);
        var seen = new HashSet<int> { root.Id };
        while (pending.TryDequeue(out var parent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!children.TryGetValue(parent.Id, out var ids))
                continue;
            foreach (var pid in ids)
            {
                if (!seen.Add(pid))
                    continue;
                Process? process = null;
                try
                {
                    process = Process.GetProcessById(pid);
                    _ = process.SafeHandle;
                    var startedUtc = process.StartTime.ToUniversalTime();
                    if (startedUtc < parent.StartTime.ToUniversalTime() || startedUtc > snapshotStartedUtc)
                        throw new IOException("Build descendant identity changed during capture.");
                    descendants.Add(process);
                    pending.Enqueue(process);
                    process = null;
                }
                catch (ArgumentException) { } // Snapshot child already exited.
                finally { process?.Dispose(); }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        internal uint Size;
        internal uint Usage;
        internal uint ProcessId;
        internal UIntPtr DefaultHeapId;
        internal uint ModuleId;
        internal uint Threads;
        internal uint ParentProcessId;
        internal int BasePriority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string? ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}

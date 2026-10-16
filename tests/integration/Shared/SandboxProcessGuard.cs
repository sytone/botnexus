using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BotNexus.Integration.Testing;

/// <summary>
/// Keeps an integration fixture's child gateway and its sandbox directory from outliving the test
/// host that created them.
/// </summary>
/// <remarks>
/// <para>
/// Both integration fixtures start a real gateway process and clean it up in <c>DisposeAsync</c>.
/// That covers the run finishing. It does not cover the run being <i>stopped</i> - a Ctrl-C, a CI
/// step timing out, a <c>pkill</c> - and when the test host dies that way the gateway is orphaned
/// and its sandbox is never deleted. Observed in practice: two abandoned gateways still running
/// four hours later, and a gigabyte of sandbox directories under the temp root.
/// </para>
/// <para>
/// Two mechanisms, because no single one covers every way a process can die:
/// </para>
/// <list type="number">
/// <item><description><see cref="KillOnExit"/> - kills registered children when this process is
/// asked to stop. Catches <c>SIGTERM</c> (what <c>pkill</c> and most CI cancellations send),
/// <c>SIGINT</c> (Ctrl-C), <c>SIGQUIT</c>, and ordinary exit.</description></item>
/// <item><description><see cref="ReapStaleSandboxes"/> - run at fixture start-up, deletes
/// sandboxes whose owning test host is gone, killing any gateway they left behind. This is what
/// covers <c>SIGKILL</c> and hard crashes, where by definition no handler of ours can run. It
/// cleans up after the <i>previous</i> run rather than the current one, which is the only thing
/// that is possible.</description></item>
/// </list>
/// <para>
/// Liveness is checked by process id <i>and</i> start time, mirroring <c>GatewayPidFile</c>: a bare
/// pid check would confuse a recycled id for the original process, and here that would mean
/// deleting a live run's sandbox out from under it.
/// </para>
/// </remarks>
internal static class SandboxProcessGuard
{
    /// <summary>Written into a sandbox root so a later run can tell whether it is abandoned.</summary>
    internal const string MarkerFileName = "sandbox-owner.json";

    /// <summary>
    /// A sandbox younger than this is never reaped, however it looks. It guards the window between
    /// a fixture creating its directory and writing the marker into it - without the floor, a run
    /// starting concurrently could delete a sibling's sandbox in that gap.
    /// </summary>
    internal static readonly TimeSpan MinimumAgeBeforeReaping = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<int, Process> Registered = new();
    private static readonly object HookLock = new();
    private static bool _hooksInstalled;
    private static readonly List<PosixSignalRegistration> SignalRegistrations = [];

    /// <summary>
    /// Registers a child so it is killed if this process exits or is asked to stop. Safe to call
    /// more than once for the same process.
    /// </summary>
    public static void KillOnExit(Process child)
    {
        ArgumentNullException.ThrowIfNull(child);
        InstallHooks();
        Registered[child.Id] = child;

        // Stop tracking a child that exits on its own, so the registry does not grow across a long
        // run and so we never try to kill a recycled id.
        try
        {
            child.EnableRaisingEvents = true;
            child.Exited += (_, _) => Registered.TryRemove(child.Id, out _);
        }
        catch (InvalidOperationException)
        {
            // Already exited between Start and here; nothing to track.
            Registered.TryRemove(child.Id, out _);
        }
    }

    /// <summary>
    /// Writes the ownership marker into a freshly created sandbox. Call immediately after creating
    /// the directory and before putting anything expensive in it.
    /// </summary>
    public static void MarkSandboxOwner(string sandboxRoot)
    {
        try
        {
            Directory.CreateDirectory(sandboxRoot);
            using var self = Process.GetCurrentProcess();
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["ownerPid"] = self.Id,
                ["ownerStartTimeUtcTicks"] = SafeStartTimeTicks(self),
                ["createdUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            });
            File.WriteAllText(Path.Combine(sandboxRoot, MarkerFileName), payload);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing marker only costs us precision in the reaper - it must never fail a run.
        }
    }

    /// <summary>
    /// Records the gateway a sandbox owns, so the reaper can kill it if this run is later found to
    /// have been abandoned.
    /// </summary>
    public static void RecordSandboxGateway(string sandboxRoot, int gatewayPid)
    {
        var path = Path.Combine(sandboxRoot, MarkerFileName);
        try
        {
            long? startTicks;
            try
            {
                using var gateway = Process.GetProcessById(gatewayPid);
                startTicks = SafeStartTimeTicks(gateway);
            }
            catch (ArgumentException)
            {
                startTicks = null;   // Already gone; the pid alone is still worth recording.
            }

            var existing = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)) ?? []
                : [];

            var payload = new Dictionary<string, object?>();
            foreach (var (key, value) in existing)
                payload[key] = value;

            payload["gatewayPid"] = gatewayPid;
            payload["gatewayStartTimeUtcTicks"] = startTicks;

            File.WriteAllText(path, JsonSerializer.Serialize(payload));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    /// <summary>
    /// Deletes sandboxes under <paramref name="familyRoot"/> whose owning test host is gone, first
    /// killing any gateway they left running. Returns the number of sandboxes reclaimed.
    /// </summary>
    /// <param name="familyRoot">e.g. <c>{temp}/botnexus-e2e</c>.</param>
    /// <param name="now">Injectable clock, for tests.</param>
    public static int ReapStaleSandboxes(string familyRoot, DateTime? now = null)
        => ReapStaleSandboxes(familyRoot, now, OpenRecordedProcess);

    // Per-call injection avoids shared mutable test hooks and never falls back to real PIDs.
    internal static int ReapStaleSandboxes(
        string familyRoot, DateTime? now, Func<int, IRecordedProcess> openProcess)
    {
        if (!Directory.Exists(familyRoot))
            return 0;

        var reaped = 0;
        var utcNow = now ?? DateTime.UtcNow;

        foreach (var sandbox in SafeEnumerateDirectories(familyRoot))
        {
            try
            {
                if (utcNow - SandboxCreatedUtc(sandbox) < MinimumAgeBeforeReaping)
                    continue;

                if (!IsAbandoned(sandbox, openProcess))
                    continue;

                if (!TryStopRecordedGateway(sandbox, openProcess))
                    continue;
                Directory.Delete(sandbox, recursive: true);
                reaped++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another run may be deleting it concurrently, or a file may be held open.
                // Reclaiming disk is a courtesy; failing a test run over it is not.
            }
        }

        return reaped;
    }

    /// <summary>
    /// When the sandbox was created, for the age floor.
    /// </summary>
    /// <remarks>
    /// Read from the marker rather than from the directory. Creation time is not portable - on
    /// Linux it is frequently unreadable or unsettable, which made the floor silently ineffective
    /// there - and last-write time is worse than useless for this: an abandoned sandbox whose
    /// orphaned gateway is still writing logs would look permanently fresh and never be reaped,
    /// which is exactly the case this class exists for. The marker's timestamp is written once and
    /// never touched again.
    ///
    /// A sandbox with no readable marker falls back to last-write time. That is the pre-guard case,
    /// where nothing better exists, and those directories are static by definition.
    /// </remarks>
    private static DateTime SandboxCreatedUtc(string sandbox)
    {
        var marker = ReadMarker(sandbox);
        if (marker is not null
            && marker.TryGetValue("createdUtc", out var created)
            && created.ValueKind == JsonValueKind.String
            // RoundtripKind alone: the marker is written with "O", which carries the offset, and
            // RoundtripKind cannot be combined with AdjustToUniversal. Normalise afterwards.
            && DateTime.TryParse(
                created.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        try
        {
            return Directory.GetLastWriteTimeUtc(sandbox);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.UtcNow;   // Unknown age: treat as new, i.e. do not reap.
        }
    }

    /// <summary>
    /// True when the sandbox's owning test host is no longer running. A sandbox with no readable
    /// marker is treated as abandoned - it is past the age floor, so it predates this run.
    /// </summary>
    private static bool IsAbandoned(string sandbox, Func<int, IRecordedProcess> openProcess)
    {
        var marker = ReadMarker(sandbox);
        if (marker is null)
            return true;

        return !IsOwnerPossiblyAlive(
            GetInt(marker, "ownerPid"),
            GetLong(marker, "ownerStartTimeUtcTicks"), openProcess);
    }

    // True means the recorded child is gone, not merely that a kill was attempted.
    private static bool TryStopRecordedGateway(string sandbox, Func<int, IRecordedProcess> openProcess)
    {
        var marker = ReadMarker(sandbox);
        var pid = GetInt(marker, "gatewayPid");
        if (pid is null or <= 0)
            return true;

        IRecordedProcess gateway;
        try
        {
            gateway = openProcess(pid.Value);
        }
        catch (ArgumentException)
        {
            return true; // No such process. This is the only PID-lookup failure proving absence.
        }
        catch (Exception ex) when (IsProcessInspectionFailure(ex))
        {
            return false;
        }

        using (gateway)
        {
            try
            {
                if (gateway.HasExited)
                    return true;

                var recordedStart = GetLong(marker, "gatewayStartTimeUtcTicks");
                if (recordedStart is null or <= 0)
                    return false;

                var actualStart = gateway.StartTimeUtcTicks;
                if (actualStart is null or <= 0)
                    return false;
                if (actualStart != recordedStart)
                    return true; // Original child is gone; never signal the recycled PID's occupant.

                // Identity verification and signaling use this same opened process, with no PID reopen.
                gateway.Kill();
                return gateway.WaitForExit(milliseconds: 5000);
            }
            catch (Exception ex) when (IsProcessInspectionFailure(ex) || ex is AggregateException)
            {
                // Tree kills can aggregate failures from individual processes. Preserve the
                // sandbox even if part of the tree was stopped, and continue with other sandboxes.
                return false; // Unknown identity, failed signal or unconfirmed exit: keep evidence for retry.
            }
        }
    }

    private static Dictionary<string, JsonElement>? ReadMarker(string sandbox)
    {
        try
        {
            var path = Path.Combine(sandbox, MarkerFileName);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Process id plus start time. The start time is what stops a recycled id from being mistaken
    /// for the original process - which here would mean sparing a dead run's sandbox forever, or
    /// worse, killing an unrelated process that inherited the id.
    /// </summary>
    private static bool IsOwnerPossiblyAlive(
        int? pid, long? startTimeUtcTicks, Func<int, IRecordedProcess> openProcess)
    {
        if (pid is null or <= 0)
            return false;

        IRecordedProcess process;
        try
        {
            process = openProcess(pid.Value);
        }
        catch (ArgumentException)
        {
            return false; // No such process.
        }
        catch (Exception ex) when (IsProcessInspectionFailure(ex))
        {
            return true;
        }

        using (process)
        {
            try
            {
                if (process.HasExited)
                    return false;
                if (startTimeUtcTicks is null or <= 0)
                    return true;

                var actual = process.StartTimeUtcTicks;
                return actual is null or <= 0 || actual == startTimeUtcTicks;
            }
            catch (Exception ex) when (IsProcessInspectionFailure(ex))
            {
                return true; // An unreadable owner must never make a live sandbox look abandoned.
            }
        }
    }

    private static bool IsProcessInspectionFailure(Exception ex)
        => ex is InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException;

    // Narrow OS boundary: verification and signaling use one Process instance. Windows binds
    // its native handle; Unix signals remain PID-based, so this reduces but cannot eliminate races.
    internal interface IRecordedProcess : IDisposable
    {
        bool HasExited { get; }
        long? StartTimeUtcTicks { get; }
        void Kill();
        bool WaitForExit(int milliseconds);
    }

    private static IRecordedProcess OpenRecordedProcess(int pid)
    {
        var process = Process.GetProcessById(pid);
        try
        {
            // On Windows, bind the native process handle before reading identity so Kill cannot
            // defer opening a different occupant of a recycled PID. Unix .NET still signals by PID;
            // keeping one Process instance avoids our own reopen but is not an atomic identity kill.
            _ = process.Handle;
            return new RecordedProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private sealed class RecordedProcess(Process process) : IRecordedProcess
    {
        public bool HasExited => process.HasExited;
        public long? StartTimeUtcTicks => SafeStartTimeTicks(process);
        public void Kill() => process.Kill(entireProcessTree: true);
        public bool WaitForExit(int milliseconds) => process.WaitForExit(milliseconds);
        public void Dispose() => process.Dispose();
    }

    private static long? SafeStartTimeTicks(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static int? GetInt(Dictionary<string, JsonElement>? marker, string key)
        => marker is not null && marker.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.Number
            && e.TryGetInt32(out var value) ? value
            : null;

    private static long? GetLong(Dictionary<string, JsonElement>? marker, string key)
        => marker is not null && marker.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.Number
            && e.TryGetInt64(out var value) ? value
            : null;

    private static void InstallHooks()
    {
        if (_hooksInstalled)
            return;

        lock (HookLock)
        {
            if (_hooksInstalled)
                return;

            AppDomain.CurrentDomain.ProcessExit += (_, _) => KillRegisteredChildren();

            // SIGTERM is the one that matters most: it is what pkill sends by default and what CI
            // cancellation usually sends, and it is precisely the case that orphaned the gateways
            // this class exists to stop orphaning. Cancel is left false so termination proceeds as
            // the sender intended - we are cleaning up, not refusing to die.
            foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT })
            {
                try
                {
                    SignalRegistrations.Add(PosixSignalRegistration.Create(signal, _ => KillRegisteredChildren()));
                }
                catch (PlatformNotSupportedException)
                {
                    // SIGQUIT is not available on Windows; ProcessExit still covers ordinary exit.
                }
            }

            _hooksInstalled = true;
        }
    }

    /// <summary>Kills every registered child. Internal so a test can drive it directly.</summary>
    internal static void KillRegisteredChildren()
    {
        foreach (var (pid, process) in Registered.ToArray())
        {
            Registered.TryRemove(pid, out _);
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
            }
        }
    }
}

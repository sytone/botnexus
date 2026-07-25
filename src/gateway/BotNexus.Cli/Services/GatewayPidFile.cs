using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace BotNexus.Cli.Services;

/// <summary>
/// Outcome of comparing a PID-file identity record against a live OS process.
/// </summary>
public enum GatewayIdentityMatch
{
    /// <summary>The PID file carries no identity (legacy bare-PID format); identity cannot be proven.</summary>
    Unverifiable,

    /// <summary>The live process matches the recorded identity — it really is the gateway we started.</summary>
    Match,

    /// <summary>The live process does NOT match the recorded identity — the PID was recycled.</summary>
    Mismatch,
}

/// <summary>
/// The contents of <c>gateway.pid</c>: a PID plus enough process identity to prove, later, that the
/// PID still refers to the very same process we spawned.
/// <para>
/// A bare PID is not an identity. Operating systems recycle PIDs, so after a crash, power loss or
/// container restart a stale <c>gateway.pid</c> can name a completely unrelated process owned by the
/// same user. Killing that PID blind (issue #2369) terminates a third-party process. Recording the
/// process start time — which is unique per (PID, start time) pair for all practical purposes —
/// together with the process name lets us refuse to kill anything we cannot positively identify.
/// </para>
/// </summary>
/// <param name="Pid">The operating system process id.</param>
/// <param name="StartTimeUtc">Process start time in UTC, or <c>null</c> for legacy files.</param>
/// <param name="ProcessName">The OS process name (e.g. <c>dotnet</c>), or <c>null</c> for legacy files.</param>
/// <param name="MainModulePath">Main module path, best-effort; may be <c>null</c> when unreadable.</param>
public sealed record GatewayPidRecord(
    int Pid,
    DateTime? StartTimeUtc,
    string? ProcessName,
    string? MainModulePath)
{
    /// <summary>
    /// True when the record carries enough information to positively identify a live process.
    /// Legacy bare-PID files return false and must never be used to authorise a kill.
    /// </summary>
    public bool HasIdentity => StartTimeUtc is not null && !string.IsNullOrWhiteSpace(ProcessName);
}

/// <summary>
/// Serialisation and verification helpers for the gateway PID file.
/// </summary>
public static class GatewayPidFile
{
    /// <summary>
    /// Start times read at different moments can differ by sub-tick rounding on some platforms, so
    /// compare with a small tolerance. This stays recycling-safe: a recycled PID would have to be
    /// re-issued to a process with the SAME name within one second of the original's start.
    /// </summary>
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Captures the identity of a live process for persistence. Falls back to a PID-only record if
    /// the OS refuses to hand over start time or module details (permissions, races, WOW64).
    /// </summary>
    public static GatewayPidRecord Capture(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        var pid = process.Id;
        DateTime? startTimeUtc = null;
        string? processName = null;
        string? mainModulePath = null;

        try
        {
            startTimeUtc = process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Leave null — the record degrades to unverifiable, which is fail-safe.
        }

        try
        {
            processName = process.ProcessName;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
        }

        try
        {
            mainModulePath = process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
        }

        return new GatewayPidRecord(pid, startTimeUtc, processName, mainModulePath);
    }

    /// <summary>
    /// Serialises a record to the on-disk JSON form. Start time is written as round-trippable UTC ticks.
    /// </summary>
    public static string Serialize(GatewayPidRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var payload = new Dictionary<string, object?>
        {
            ["pid"] = record.Pid,
            ["startTimeUtcTicks"] = record.StartTimeUtc?.Ticks,
            ["processName"] = record.ProcessName,
            ["mainModulePath"] = record.MainModulePath,
        };

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Parses PID file content. Accepts both the identity-bearing JSON form and the legacy bare-PID
    /// form (a file containing just <c>1234</c>), which yields a record with no identity.
    /// </summary>
    /// <returns><c>true</c> when a usable PID was parsed.</returns>
    public static bool TryParse(string? content, out GatewayPidRecord? record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var trimmed = content.Trim();

        // Legacy format: the whole file is a bare PID integer.
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var barePid))
        {
            if (barePid <= 0)
                return false;
            record = new GatewayPidRecord(barePid, null, null, null);
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (!root.TryGetProperty("pid", out var pidElement)
                || !pidElement.TryGetInt32(out var pid)
                || pid <= 0)
            {
                return false;
            }

            DateTime? startTimeUtc = null;
            if (root.TryGetProperty("startTimeUtcTicks", out var ticksElement)
                && ticksElement.ValueKind == JsonValueKind.Number
                && ticksElement.TryGetInt64(out var ticks)
                && ticks > 0
                && ticks <= DateTime.MaxValue.Ticks)
            {
                startTimeUtc = new DateTime(ticks, DateTimeKind.Utc);
            }

            var processName = ReadOptionalString(root, "processName");
            var mainModulePath = ReadOptionalString(root, "mainModulePath");

            record = new GatewayPidRecord(pid, startTimeUtc, processName, mainModulePath);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadOptionalString(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>
    /// Compares a persisted record against a live process.
    /// Returns <see cref="GatewayIdentityMatch.Unverifiable"/> for legacy records or when the OS
    /// will not disclose the live process's start time — callers MUST treat that as "do not kill".
    /// </summary>
    public static GatewayIdentityMatch Verify(GatewayPidRecord record, Process process)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(process);

        if (!record.HasIdentity)
            return GatewayIdentityMatch.Unverifiable;

        if (record.Pid != process.Id)
            return GatewayIdentityMatch.Mismatch;

        var live = Capture(process);
        if (live.StartTimeUtc is null || string.IsNullOrWhiteSpace(live.ProcessName))
            return GatewayIdentityMatch.Unverifiable;

        if (!string.Equals(record.ProcessName, live.ProcessName, StringComparison.OrdinalIgnoreCase))
            return GatewayIdentityMatch.Mismatch;

        var delta = record.StartTimeUtc!.Value - live.StartTimeUtc.Value;
        if (delta < TimeSpan.Zero)
            delta = delta.Negate();

        return delta <= StartTimeTolerance
            ? GatewayIdentityMatch.Match
            : GatewayIdentityMatch.Mismatch;
    }
}

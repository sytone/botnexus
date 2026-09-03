using System.Diagnostics;
using System.Text.Json;
using BotNexus.Integration.Testing;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// The guard exists because a test host that is stopped rather than finished leaves its gateway
/// running and its sandbox on disk. These cover both halves: killing the child when this process is
/// asked to stop, and reclaiming what an earlier run abandoned.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SandboxProcessGuardTests : IDisposable
{
    private readonly string _familyRoot = Directory.CreateTempSubdirectory("guard-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_familyRoot, recursive: true); } catch (IOException) { }
    }

    private string NewSandbox(string name, DateTime? created = null)
    {
        var path = Path.Combine(_familyRoot, name);
        Directory.CreateDirectory(path);
        // Last-write time is the guard's fallback age signal for an unmarked sandbox, and unlike
        // creation time it is settable on every platform the suite runs on.
        if (created is not null)
            Directory.SetLastWriteTimeUtc(path, created.Value);
        return path;
    }

    private static void WriteMarker(
        string sandbox,
        int? ownerPid,
        long? ownerStart,
        int? gatewayPid = null,
        DateTime? createdUtc = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ownerPid"] = ownerPid,
            ["ownerStartTimeUtcTicks"] = ownerStart,
            // The guard takes a marked sandbox's age from here, precisely so an orphaned gateway
            // still writing logs cannot keep its sandbox looking fresh forever.
            ["createdUtc"] = (createdUtc ?? DateTime.UtcNow.AddHours(-2)).ToString("O"),
        };
        if (gatewayPid is not null)
            payload["gatewayPid"] = gatewayPid;

        File.WriteAllText(
            Path.Combine(sandbox, SandboxProcessGuard.MarkerFileName),
            JsonSerializer.Serialize(payload));
    }

    private static Process StartSleeper()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c timeout /t 300")
            : new ProcessStartInfo("sleep", "300");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        return Process.Start(psi)!;
    }

    // ── Reaping ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReapStaleSandboxes_OwnerStillRunning_KeepsTheSandbox()
    {
        var sandbox = NewSandbox("live");
        using var self = Process.GetCurrentProcess();
        WriteMarker(sandbox, self.Id, self.StartTime.ToUniversalTime().Ticks,
                    createdUtc: DateTime.UtcNow.AddHours(-2));

        SandboxProcessGuard.ReapStaleSandboxes(_familyRoot).ShouldBe(0);
        Directory.Exists(sandbox).ShouldBeTrue();
    }

    [Fact]
    public void ReapStaleSandboxes_OwnerGone_DeletesTheSandbox()
    {
        var sandbox = NewSandbox("abandoned", DateTime.UtcNow.AddHours(-2));
        // A pid that cannot be running: reaped immediately by the OS and never reused this fast.
        var dead = StartSleeper();
        var deadPid = dead.Id;
        dead.Kill();
        dead.WaitForExit();
        WriteMarker(sandbox, deadPid, ownerStart: null);

        SandboxProcessGuard.ReapStaleSandboxes(_familyRoot).ShouldBe(1);
        Directory.Exists(sandbox).ShouldBeFalse();
    }

    // The window between creating a sandbox and writing its marker must not let a concurrently
    // starting run delete it.
    [Fact]
    public void ReapStaleSandboxes_RecentSandbox_IsNeverReapedEvenWithoutAMarker()
    {
        var sandbox = NewSandbox("just-created");

        SandboxProcessGuard.ReapStaleSandboxes(_familyRoot).ShouldBe(0);
        Directory.Exists(sandbox).ShouldBeTrue();
    }

    [Fact]
    public void ReapStaleSandboxes_OldSandboxWithNoMarker_IsReaped()
    {
        var sandbox = NewSandbox("pre-guard", DateTime.UtcNow.AddDays(-1));

        SandboxProcessGuard.ReapStaleSandboxes(_familyRoot).ShouldBe(1);
        Directory.Exists(sandbox).ShouldBeFalse();
    }

    // Pid reuse: the recorded start time must stop a recycled id being mistaken for the owner,
    // because treating a live sandbox as abandoned would delete a running test's state.
    [Fact]
    public void ReapStaleSandboxes_PidReused_IsTreatedAsAbandoned()
    {
        var sandbox = NewSandbox("recycled", DateTime.UtcNow.AddHours(-2));
        using var self = Process.GetCurrentProcess();
        WriteMarker(sandbox, self.Id, ownerStart: 1);   // right pid, wrong start time

        SandboxProcessGuard.ReapStaleSandboxes(_familyRoot).ShouldBe(1);
        Directory.Exists(sandbox).ShouldBeFalse();
    }

    // The point of the whole exercise: an abandoned sandbox's gateway is not left running.
    [Fact]
    public void ReapStaleSandboxes_KillsTheGatewayTheAbandonedRunLeftBehind()
    {
        var orphan = StartSleeper();
        try
        {
            var sandbox = NewSandbox("with-orphan", DateTime.UtcNow.AddHours(-2));
            var dead = StartSleeper();
            var deadOwnerPid = dead.Id;
            dead.Kill();
            dead.WaitForExit();
            WriteMarker(sandbox, deadOwnerPid, ownerStart: null, gatewayPid: orphan.Id);

            SandboxProcessGuard.ReapStaleSandboxes(_familyRoot).ShouldBe(1);

            orphan.WaitForExit(milliseconds: 10_000).ShouldBeTrue("the orphaned gateway should have been killed");
        }
        finally
        {
            try { if (!orphan.HasExited) orphan.Kill(); } catch (InvalidOperationException) { }
            orphan.Dispose();
        }
    }

    [Fact]
    public void ReapStaleSandboxes_MissingFamilyRoot_IsNotAnError()
        => SandboxProcessGuard.ReapStaleSandboxes(Path.Combine(_familyRoot, "does-not-exist")).ShouldBe(0);

    // ── Kill-on-exit ─────────────────────────────────────────────────────────────────

    // Drives the handler directly. The signal path itself cannot be exercised in-process without
    // terminating the test host, so what is pinned here is that the handler kills what it tracks.
    [Fact]
    public void KillRegisteredChildren_KillsARegisteredChild()
    {
        var child = StartSleeper();
        try
        {
            SandboxProcessGuard.KillOnExit(child);

            SandboxProcessGuard.KillRegisteredChildren();

            child.WaitForExit(milliseconds: 10_000).ShouldBeTrue();
        }
        finally
        {
            try { if (!child.HasExited) child.Kill(); } catch (InvalidOperationException) { }
            child.Dispose();
        }
    }

    [Fact]
    public void KillRegisteredChildren_AfterTheChildAlreadyExited_DoesNotThrow()
    {
        var child = StartSleeper();
        SandboxProcessGuard.KillOnExit(child);
        child.Kill();
        child.WaitForExit();

        Should.NotThrow(SandboxProcessGuard.KillRegisteredChildren);
        child.Dispose();
    }

    // ── Marker round-trip ────────────────────────────────────────────────────────────

    [Fact]
    public void MarkSandboxOwner_ThenRecordGateway_KeepsBothInOneMarker()
    {
        var sandbox = NewSandbox("marker");
        SandboxProcessGuard.MarkSandboxOwner(sandbox);
        SandboxProcessGuard.RecordSandboxGateway(sandbox, gatewayPid: 4242);

        var marker = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            File.ReadAllText(Path.Combine(sandbox, SandboxProcessGuard.MarkerFileName)))!;

        using var self = Process.GetCurrentProcess();
        marker["ownerPid"].GetInt32().ShouldBe(self.Id);
        marker["gatewayPid"].GetInt32().ShouldBe(4242);
        marker.ShouldContainKey("createdUtc");
    }
}

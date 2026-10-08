using System.ComponentModel;
using System.Text.Json;
using BotNexus.Integration.Testing;

namespace BotNexus.Integration.Cli.Tests;

[Trait("Category", "Unit")]
public sealed class SandboxProcessIdentityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("guard-identity-tests").FullName;
    private const int OwnerPid = 101;
    private const int ChildPid = 102;
    private const long ChildStart = 200;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Marker(long? childStart = ChildStart, long? ownerStart = 100)
    {
        var sandbox = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        File.WriteAllText(Path.Combine(sandbox, SandboxProcessGuard.MarkerFileName),
            JsonSerializer.Serialize(new
            {
                ownerPid = OwnerPid, ownerStartTimeUtcTicks = ownerStart,
                gatewayPid = ChildPid, gatewayStartTimeUtcTicks = childStart,
                createdUtc = DateTime.UtcNow.AddHours(-2),
            }));
        return sandbox;
    }

    private int Reap(FakeProcess child, FakeProcess? owner = null, Action<int>? opening = null)
        => SandboxProcessGuard.ReapStaleSandboxes(_root, DateTime.UtcNow, pid =>
        {
            opening?.Invoke(pid);
            if (pid == OwnerPid) return owner ?? new FakeProcess { Exited = true };
            pid.ShouldBe(ChildPid, "only a recorded PID may be opened");
            return child;
        });

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void ReapStaleSandboxes_MissingOrInvalidChildIdentity_DoesNotSignalOrReclaim(long? recordedStart)
    {
        var sandbox = Marker(recordedStart);
        var child = new FakeProcess();
        Reap(child).ShouldBe(0);
        child.Kills.ShouldBe(0);
        Directory.Exists(sandbox).ShouldBeTrue();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("win32")]
    [InlineData("unsupported")]
    [InlineData("invalid")]
    public void ReapStaleSandboxes_UnreadableChildIdentity_DoesNotSignalOrReclaim(string failure)
    {
        var sandbox = Marker();
        var child = new FakeProcess { StartFailure = failure };
        Reap(child).ShouldBe(0);
        child.Kills.ShouldBe(0);
        child.Disposed.ShouldBeTrue();
        Directory.Exists(sandbox).ShouldBeTrue();
    }

    [Fact]
    public void ReapStaleSandboxes_RecycledChildPid_DoesNotSignalUnrelatedProcess()
    {
        var sandbox = Marker();
        var child = new FakeProcess { Start = ChildStart + 1 };
        Reap(child).ShouldBe(1);
        child.Kills.ShouldBe(0);
        Directory.Exists(sandbox).ShouldBeFalse();
    }

    [Theory]
    [InlineData("win32")]
    [InlineData("unsupported")]
    [InlineData("invalid")]
    public void ReapStaleSandboxes_SignalFails_PreservesSandbox(string failure)
    {
        var sandbox = Marker();
        var child = new FakeProcess { KillFailure = failure };
        Reap(child).ShouldBe(0);
        child.Kills.ShouldBe(1);
        child.Disposed.ShouldBeTrue();
        Directory.Exists(sandbox).ShouldBeTrue();
    }

    [Fact]
    public void ReapStaleSandboxes_AggregateSignalFailure_PreservesSandboxAndContinues()
    {
        var first = Marker();
        var second = Marker();
        var child = new FakeProcess { KillFailure = "aggregate" };
        var opens = new List<int>();

        Reap(child, opening: opens.Add).ShouldBe(0);

        opens.ShouldBe(new[] { OwnerPid, ChildPid, OwnerPid, ChildPid });
        child.Kills.ShouldBe(2, "a failed tree kill must not abort reaping other sandboxes");
        child.Calls.ShouldNotContain("wait");
        child.Disposed.ShouldBeTrue();
        Directory.Exists(first).ShouldBeTrue();
        Directory.Exists(second).ShouldBeTrue();
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void ReapStaleSandboxes_InvalidActualChildStart_DoesNotSignalOrReclaim(long actualStart)
    {
        var sandbox = Marker();
        var child = new FakeProcess { Start = actualStart };
        Reap(child).ShouldBe(0);
        child.Kills.ShouldBe(0);
        child.Disposed.ShouldBeTrue();
        Directory.Exists(sandbox).ShouldBeTrue();
    }

    [Fact]
    public void ReapStaleSandboxes_ExitNotConfirmed_PreservesSandbox()
    {
        var sandbox = Marker();
        var child = new FakeProcess { WaitResult = false };
        Reap(child).ShouldBe(0);
        child.Kills.ShouldBe(1);
        Directory.Exists(sandbox).ShouldBeTrue();
    }

    [Fact]
    public void ReapStaleSandboxes_PositiveIdentity_VerifiesAndSignalsSameOpenedHandleOnce()
    {
        var sandbox = Marker();
        var child = new FakeProcess();
        var opens = new List<int>();
        Reap(child, opening: opens.Add).ShouldBe(1);
        opens.ShouldBe(new[] { OwnerPid, ChildPid });
        child.Calls.ShouldBe(new[] { "exited", "start", "kill", "wait", "dispose" });
        child.Kills.ShouldBe(1);
        Directory.Exists(sandbox).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(100L)]
    public void ReapStaleSandboxes_UnknownOwnerIdentity_ConservativelyKeepsSandbox(long? recordedStart)
    {
        var sandbox = Marker(ownerStart: recordedStart);
        var child = new FakeProcess();
        var opens = new List<int>();
        Reap(child, new FakeProcess { StartFailure = "win32" }, opens.Add).ShouldBe(0);
        opens.ShouldBe(new[] { OwnerPid });
        child.Kills.ShouldBe(0);
        Directory.Exists(sandbox).ShouldBeTrue();
    }

    [Fact]
    public void ReapStaleSandboxes_ChildAlreadyExited_ReclaimsWithoutSignaling()
    {
        var sandbox = Marker(childStart: null);
        var child = new FakeProcess { Exited = true };
        Reap(child).ShouldBe(1);
        child.Kills.ShouldBe(0);
        Directory.Exists(sandbox).ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReapStaleSandboxes_ProcessOpenFails_OnlyConfirmedAbsenceAllowsReclaim(bool absent)
    {
        var sandbox = Marker();
        var count = SandboxProcessGuard.ReapStaleSandboxes(_root, DateTime.UtcNow, pid =>
        {
            if (pid == OwnerPid) return new FakeProcess { Exited = true };
            if (absent) throw new ArgumentException("No such process");
            throw new Win32Exception("Access denied");
        });
        count.ShouldBe(absent ? 1 : 0);
        Directory.Exists(sandbox).ShouldBe(!absent);
    }

    [Theory]
    [InlineData("\"unreadable\"")]
    [InlineData("9223372036854775808")]
    [InlineData("1.5")]
    public void ReapStaleSandboxes_MalformedRecordedStart_DoesNotSignalOrReclaim(string jsonStart)
    {
        var sandbox = Marker();
        var path = Path.Combine(sandbox, SandboxProcessGuard.MarkerFileName);
        var marker = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Marker was empty.");
        marker["gatewayStartTimeUtcTicks"] = JsonSerializer.Deserialize<JsonElement>(jsonStart);
        File.WriteAllText(path, JsonSerializer.Serialize(marker));
        var child = new FakeProcess();
        Reap(child).ShouldBe(0);
        child.Kills.ShouldBe(0);
        Directory.Exists(sandbox).ShouldBeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReapStaleSandboxes_ChildInspectionOrWaitFails_PreservesSandbox(bool inspection)
    {
        var sandbox = Marker();
        var child = new FakeProcess { ExitFailure = inspection ? "win32" : null,
            WaitFailure = inspection ? null : "win32" };
        Reap(child).ShouldBe(0);
        child.Kills.ShouldBe(inspection ? 0 : 1);
        Directory.Exists(sandbox).ShouldBeTrue();
    }

    [Fact]
    public void ReapStaleSandboxes_OwnerCannotBeOpened_KeepsSandboxWithoutOpeningChild()
    {
        var sandbox = Marker();
        var opened = new List<int>();
        SandboxProcessGuard.ReapStaleSandboxes(_root, DateTime.UtcNow, pid =>
        {
            opened.Add(pid);
            throw new Win32Exception("Access denied");
        }).ShouldBe(0);
        opened.ShouldBe(new[] { OwnerPid });
        Directory.Exists(sandbox).ShouldBeTrue();
    }

    [Fact]
    public void ReapStaleSandboxes_NoRecordedChild_DoesNotLookForArbitraryProcesses()
    {
        var sandbox = Marker();
        var path = Path.Combine(sandbox, SandboxProcessGuard.MarkerFileName);
        var marker = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Marker was empty.");
        marker.Remove("gatewayPid");
        File.WriteAllText(path, JsonSerializer.Serialize(marker));
        var opens = new List<int>();
        Reap(new FakeProcess(), opening: opens.Add).ShouldBe(1);
        opens.ShouldBe(new[] { OwnerPid });
        Directory.Exists(sandbox).ShouldBeFalse();
    }

    [Fact]
    public void ReapStaleSandboxes_MixedOutcomes_CountsOnlyReclaimedDirectories()
    {
        var preserved = Marker(childStart: null);
        var reclaimed = Marker();
        var child = new FakeProcess();
        Reap(child).ShouldBe(1);
        child.Kills.ShouldBe(1);
        Directory.Exists(preserved).ShouldBeTrue();
        Directory.Exists(reclaimed).ShouldBeFalse();
    }

    private sealed class FakeProcess : SandboxProcessGuard.IRecordedProcess
    {
        public bool Exited { get; init; }
        public long? Start { get; init; } = ChildStart;
        public string? StartFailure { get; init; }
        public string? KillFailure { get; init; }
        public string? ExitFailure { get; init; }
        public string? WaitFailure { get; init; }
        public bool WaitResult { get; init; } = true;
        public int Kills { get; private set; }
        public bool Disposed { get; private set; }
        public List<string> Calls { get; } = [];
        public bool HasExited { get { Calls.Add("exited"); Throw(ExitFailure); return Exited; } }
        public long? StartTimeUtcTicks
        {
            get
            {
                Calls.Add("start");
                if (StartFailure == "null") return null;
                Throw(StartFailure);
                return Start;
            }
        }
        public void Kill()
        {
            Calls.Add("kill");
            Kills++;
            Throw(KillFailure);
        }
        public bool WaitForExit(int milliseconds)
        {
            milliseconds.ShouldBe(5000);
            Calls.Add("wait");
            Throw(WaitFailure);
            return WaitResult;
        }
        public void Dispose() { Calls.Add("dispose"); Disposed = true; }
        private static void Throw(string? failure)
        {
            if (failure == "aggregate") throw new AggregateException(new Win32Exception("Access denied"));
            if (failure == "win32") throw new Win32Exception("Access denied");
            if (failure == "unsupported") throw new NotSupportedException();
            if (failure == "invalid") throw new InvalidOperationException();
        }
    }
}

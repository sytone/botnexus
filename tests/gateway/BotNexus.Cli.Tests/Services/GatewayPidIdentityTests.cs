using System.Diagnostics;
using BotNexus.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Cli.Tests.Services;

/// <summary>
/// Covers the PID-file process-identity guard added for issue #2369.
/// <para>
/// The security property under test: <c>botnexus gateway stop</c> must never send a kill signal to a
/// PID it cannot positively identify as the gateway it started. A stale <c>gateway.pid</c> whose PID
/// has been recycled onto an unrelated process, and a legacy bare-PID file with no identity at all,
/// must both resolve to "not running" with the PID file cleaned up — and with the victim process
/// still alive afterwards.
/// </para>
/// </summary>
public sealed class GatewayPidIdentityTests : IDisposable
{
    private readonly string _home;
    private readonly GatewayProcessManager _manager;
    private readonly IHealthChecker _healthChecker;

    public GatewayPidIdentityTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"botnexus-pididentity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);
        _healthChecker = Substitute.For<IHealthChecker>();
        _manager = new GatewayProcessManager(_healthChecker, NullLogger<GatewayProcessManager>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }

    private string PidFilePath => Path.Combine(_home, "gateway.pid");

    // ---------------------------------------------------------------------
    // GatewayPidFile round-trip and parsing
    // ---------------------------------------------------------------------

    [Fact]
    public void Capture_RecordsIdentityForLiveProcess()
    {
        var record = GatewayPidFile.Capture(Process.GetCurrentProcess());

        record.Pid.ShouldBe(Environment.ProcessId);
        record.HasIdentity.ShouldBeTrue();
        record.StartTimeUtc.ShouldNotBeNull();
        record.StartTimeUtc!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        record.ProcessName.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SerializeThenParse_RoundTripsIdentityExactly()
    {
        var original = GatewayPidFile.Capture(Process.GetCurrentProcess());

        var parsed = ParseOrFail(GatewayPidFile.Serialize(original));

        parsed.Pid.ShouldBe(original.Pid);
        parsed.ProcessName.ShouldBe(original.ProcessName);
        parsed.MainModulePath.ShouldBe(original.MainModulePath);
        // Round-trippable UTC ticks: no precision loss at all.
        parsed.StartTimeUtc.ShouldBe(original.StartTimeUtc);
        parsed.HasIdentity.ShouldBeTrue();
    }

    [Fact]
    public void TryParse_LegacyBarePid_YieldsRecordWithoutIdentity()
    {
        var parsed = ParseOrFail("4321\n");

        parsed.Pid.ShouldBe(4321);
        parsed.HasIdentity.ShouldBeFalse();
        parsed.StartTimeUtc.ShouldBeNull();
        parsed.ProcessName.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-pid")]
    [InlineData("{ this is not json")]
    [InlineData("{\"startTimeUtcTicks\":123}")]
    [InlineData("[1,2,3]")]
    public void TryParse_RejectsUnusableContent(string content)
    {
        GatewayPidFile.TryParse(content, out var record).ShouldBeFalse();
        record.ShouldBeNull();
    }

    [Fact]
    public void Verify_LegacyRecord_IsUnverifiable_NeverAMatch()
    {
        var current = Process.GetCurrentProcess();
        var legacy = new GatewayPidRecord(current.Id, null, null, null);

        // Even though the PID genuinely IS this live process, a legacy record cannot prove it.
        GatewayPidFile.Verify(legacy, current).ShouldBe(GatewayIdentityMatch.Unverifiable);
    }

    [Fact]
    public void Verify_MatchingIdentity_ReturnsMatch()
    {
        var current = Process.GetCurrentProcess();

        GatewayPidFile.Verify(GatewayPidFile.Capture(current), current)
            .ShouldBe(GatewayIdentityMatch.Match);
    }

    [Fact]
    public void Verify_DifferentStartTime_ReturnsMismatch()
    {
        var current = Process.GetCurrentProcess();
        var genuine = GatewayPidFile.Capture(current);
        // Simulate PID recycling: same PID and name, but the live process started much later.
        var recycled = genuine with { StartTimeUtc = genuine.StartTimeUtc!.Value.AddHours(-3) };

        GatewayPidFile.Verify(recycled, current).ShouldBe(GatewayIdentityMatch.Mismatch);
    }

    [Fact]
    public void Verify_DifferentProcessName_ReturnsMismatch()
    {
        var current = Process.GetCurrentProcess();
        var genuine = GatewayPidFile.Capture(current);
        var recycled = genuine with { ProcessName = "definitely-not-the-gateway" };

        GatewayPidFile.Verify(recycled, current).ShouldBe(GatewayIdentityMatch.Mismatch);
    }

    // ---------------------------------------------------------------------
    // StopAsync — the dangerous path
    // ---------------------------------------------------------------------

    [Fact]
    public async Task StopAsync_WhenPidRecycledOntoForeignProcess_DoesNotKillIt()
    {
        // Arrange: a REAL live third-party process stands in for the innocent victim that inherited
        // the recycled PID. The PID file claims that PID belongs to a gateway that started long ago.
        using var victim = StartLongRunningProcess();
        var genuine = GatewayPidFile.Capture(victim);
        // Guard against a vacuous test: the simulated condition must actually be constructible.
        genuine.HasIdentity.ShouldBeTrue("victim identity must be readable for this test to mean anything");

        var stalePidFileContent = GatewayPidFile.Serialize(
            genuine with { StartTimeUtc = genuine.StartTimeUtc!.Value.AddHours(-6) });
        await File.WriteAllTextAsync(PidFilePath, stalePidFileContent);

        // Sanity: the mismatch condition really does fire for this record.
        GatewayPidFile.Verify(ParseOrFail(stalePidFileContent), victim)
            .ShouldBe(GatewayIdentityMatch.Mismatch);

        try
        {
            // Act
            var result = await _manager.StopAsync(_home);

            // Assert: reported as not running, PID file cleaned, victim UNHARMED.
            result.Success.ShouldBeTrue();
            result.Message.ShouldNotBeNull();
            result.Message!.ShouldContain("recycled");
            File.Exists(PidFilePath).ShouldBeFalse();

            victim.Refresh();
            victim.HasExited.ShouldBeFalse("a recycled PID must NEVER be killed");
        }
        finally
        {
            KillQuietly(victim);
        }
    }

    [Fact]
    public async Task StopAsync_WhenLegacyBarePidFile_DoesNotKillTheProcess()
    {
        // Arrange: an old-format pid file containing only a bare PID that happens to be live.
        using var victim = StartLongRunningProcess();
        await File.WriteAllTextAsync(PidFilePath, victim.Id.ToString());

        try
        {
            var result = await _manager.StopAsync(_home);

            // Documented choice: unverifiable => clean up and report not running, never kill.
            result.Success.ShouldBeTrue();
            result.Message.ShouldNotBeNull();
            result.Message!.ShouldContain("not running");
            File.Exists(PidFilePath).ShouldBeFalse();

            victim.Refresh();
            victim.HasExited.ShouldBeFalse("an unverifiable legacy PID must NEVER be killed");
        }
        finally
        {
            KillQuietly(victim);
        }
    }

    [Fact]
    public async Task StopAsync_WhenIdentityMatches_KillsTheProcess()
    {
        // Happy path: identity written by StartAsync matches the live process, so the kill proceeds.
        using var target = StartLongRunningProcess();
        var record = GatewayPidFile.Capture(target);
        record.HasIdentity.ShouldBeTrue();
        await File.WriteAllTextAsync(PidFilePath, GatewayPidFile.Serialize(record));

        GatewayPidFile.Verify(record, target).ShouldBe(GatewayIdentityMatch.Match);

        try
        {
            var result = await _manager.StopAsync(_home);

            result.Success.ShouldBeTrue();
            result.Message.ShouldNotBeNull();
            result.Message!.ShouldContain("stopped");
            File.Exists(PidFilePath).ShouldBeFalse();

            target.Refresh();
            target.HasExited.ShouldBeTrue("a verified gateway PID must be killed");
        }
        finally
        {
            KillQuietly(target);
        }
    }

    [Fact]
    public async Task StopAsync_WhenNoPidFile_ReportsNotRunning()
    {
        File.Exists(PidFilePath).ShouldBeFalse();

        var result = await _manager.StopAsync(_home);

        result.Success.ShouldBeTrue();
        result.Message.ShouldNotBeNull();
        result.Message!.ShouldContain("not running");
    }

    [Fact]
    public async Task StopAsync_WhenProcessAlreadyExited_CleansStalePidWithoutKilling()
    {
        var exited = StartLongRunningProcess();
        var record = GatewayPidFile.Capture(exited);
        KillQuietly(exited);
        exited.WaitForExit(5000).ShouldBeTrue("the stand-in process must actually have exited");
        var deadPid = record.Pid;
        exited.Dispose();

        await File.WriteAllTextAsync(PidFilePath, GatewayPidFile.Serialize(record));

        var result = await _manager.StopAsync(_home);

        result.Success.ShouldBeTrue();
        result.Message.ShouldNotBeNull();
        result.Message!.ShouldContain(deadPid.ToString());
        File.Exists(PidFilePath).ShouldBeFalse();
    }

    // ---------------------------------------------------------------------
    // Status / IsRunning must not report a stranger as the gateway
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetStatusAsync_WhenPidRecycledOntoForeignProcess_ReportsNotRunning()
    {
        using var victim = StartLongRunningProcess();
        var genuine = GatewayPidFile.Capture(victim);
        await File.WriteAllTextAsync(
            PidFilePath,
            GatewayPidFile.Serialize(genuine with { StartTimeUtc = genuine.StartTimeUtc!.Value.AddHours(-6) }));

        try
        {
            var status = await _manager.GetStatusAsync(_home);

            status.State.ShouldBe(GatewayState.NotRunning);
            status.Pid.ShouldBeNull();
            File.Exists(PidFilePath).ShouldBeFalse();

            victim.Refresh();
            victim.HasExited.ShouldBeFalse();
        }
        finally
        {
            KillQuietly(victim);
        }
    }

    [Fact]
    public async Task IsRunning_WhenLegacyBarePidFile_ReturnsFalseAndCleansUp()
    {
        using var victim = StartLongRunningProcess();
        await File.WriteAllTextAsync(PidFilePath, victim.Id.ToString());

        try
        {
            _manager.IsRunning(_home).ShouldBeFalse();
            File.Exists(PidFilePath).ShouldBeFalse();

            victim.Refresh();
            victim.HasExited.ShouldBeFalse();
        }
        finally
        {
            KillQuietly(victim);
        }
    }

    [Fact]
    public async Task IsRunning_WhenIdentityMatches_ReturnsTrue()
    {
        await File.WriteAllTextAsync(
            PidFilePath,
            GatewayPidFile.Serialize(GatewayPidFile.Capture(Process.GetCurrentProcess())));

        _manager.IsRunning(_home).ShouldBeTrue();
        File.Exists(PidFilePath).ShouldBeTrue();
    }

    [Fact]
    public async Task StartAsync_WritesIdentityBearingPidFile()
    {
        _healthChecker
            .WaitForHealthyAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var options = new GatewayStartOptions(
            ExecutablePath: "BotNexus.Gateway.Api.dll",
            Arguments: null,
            HomePath: _home,
            HealthUrl: "http://localhost:6199/health");

        var result = await _manager.StartAsync(options);
        result.Success.ShouldBeTrue();

        var written = ParseOrFail(await File.ReadAllTextAsync(PidFilePath));
        written.HasIdentity.ShouldBeTrue("StartAsync must persist process identity, not a bare PID");
        ((int?)written.Pid).ShouldBe(result.Pid);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static GatewayPidRecord ParseOrFail(string content)
    {
        GatewayPidFile.TryParse(content, out var record).ShouldBeTrue($"Expected parsable PID content: {content}");
        return record ?? throw new InvalidOperationException("Expected a parsed PID record.");
    }

    private static Process StartLongRunningProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c timeout /t 60 /nobreak" : "-c \"sleep 60\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start stand-in process for the test.");
        // Touch the identity fields once so the OS caches them before any kill.
        _ = process.Id;
        return process;
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            process.Refresh();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}

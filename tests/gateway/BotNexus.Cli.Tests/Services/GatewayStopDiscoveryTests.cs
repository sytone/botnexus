using BotNexus.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Cli.Tests.Services;

/// <summary>
/// Pins the PID-file-less gateway discovery and the observed-outcome reporting introduced for
/// issue #2772, and the #2369 never-signal-an-unidentified-process guarantee it must not weaken.
///
/// Every process here is an <see cref="IGatewayProcessHandle"/> fake: nothing is spawned,
/// enumerated or signalled for real, so the security assertions are exact rather than incidental.
/// </summary>
public sealed class GatewayStopDiscoveryTests : IDisposable
{
    private readonly string _home;
    private readonly IHealthChecker _healthChecker = Substitute.For<IHealthChecker>();

    public GatewayStopDiscoveryTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"bn-2772-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home))
                Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A scripted live process. Records whether it was signalled; never touches the OS.</summary>
    private sealed class FakeProcessHandle(int id, string? executablePath, bool throwOnPath = false)
        : IGatewayProcessHandle
    {
        public int Id { get; } = id;

        public int KillCount { get; private set; }

        public string? ExecutablePath =>
            throwOnPath
                ? throw new InvalidOperationException("access denied reading main module")
                : executablePath;

        public void Kill() => KillCount++;

        public bool WaitForExit(int milliseconds) => true;
    }

    private GatewayProcessManager NewManager(params IGatewayProcessHandle[] processes)
        => new(
            _healthChecker,
            NullLogger<GatewayProcessManager>.Instance,
            processEnumerator: () => processes);

    /// <summary>The managed DLL path this deployment would launch. Never created on disk - discovery
    /// is a path-identity comparison, not a file probe.</summary>
    private string GatewayDll => Path.Combine(_home, "bin", "BotNexus.Gateway.Api.dll");

    private string GatewayApphostExe => Path.Combine(_home, "bin", "BotNexus.Gateway.Api.exe");

    private string PidFilePath => Path.Combine(_home, "gateway.pid");

    // -------------------------------------------------------------------------------------
    // AC1: the two outcomes are distinguishable at the manager boundary.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task AC1_StopAsync_ReportsNotRunning_WhenNoPidFileAndNoDiscoverableProcess()
    {
        File.Exists(PidFilePath).ShouldBeFalse("the fixture must start with no PID file");

        var manager = NewManager(new FakeProcessHandle(11, @"C:\windows\system32\notepad.exe"));

        var result = await manager.StopAsync(_home, GatewayDll, CancellationToken.None);

        result.Outcome.ShouldBe(GatewayStopOutcome.NotRunning,
            "nothing was found, so nothing was stopped - Success alone cannot express that (#2772)");
    }

    [Fact]
    public async Task AC1_StopAsync_ReportsStopped_WhenALiveGatewayWasFoundAndKilled()
    {
        var gateway = new FakeProcessHandle(4242, GatewayDll);
        var manager = NewManager(gateway);

        var result = await manager.StopAsync(_home, GatewayDll, CancellationToken.None);

        result.Outcome.ShouldBe(GatewayStopOutcome.Stopped);
        gateway.KillCount.ShouldBe(1);
    }

    // -------------------------------------------------------------------------------------
    // AC2: discovery by binary path when there is no PID file at all.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task AC2_StopAsync_DiscoversAndKillsGateway_ByManagedDllPath_WithNoPidFile()
    {
        var gateway = new FakeProcessHandle(777, GatewayDll);
        var manager = NewManager(
            new FakeProcessHandle(1, @"C:\other\dotnet.exe"),
            gateway);

        var result = await manager.StopAsync(_home, GatewayDll, CancellationToken.None);

        gateway.KillCount.ShouldBe(1, "the live gateway must be discovered without a PID file (#2772)");
        result.Outcome.ShouldBe(GatewayStopOutcome.Stopped);
        result.Message.ShouldNotBeNull();
        result.Message!.ShouldContain("777");
    }

    [Fact]
    public async Task AC2_StopAsync_DiscoversAndKillsGateway_ByApphostExeBesideTheDll()
    {
        // StartAsync prefers the native apphost, so the LIVE process image is the .exe even though
        // the deployment identifies the gateway by its managed DLL path.
        var apphost = new FakeProcessHandle(888, GatewayApphostExe);
        var manager = NewManager(apphost);

        var result = await manager.StopAsync(_home, GatewayDll, CancellationToken.None);

        apphost.KillCount.ShouldBe(1, "the apphost beside the DLL is the same gateway binary");
        result.Outcome.ShouldBe(GatewayStopOutcome.Stopped);
    }

    [Fact]
    public void AC2_BuildGatewayPathCandidates_IncludesTheDllAndTheApphostBesideIt()
    {
        var candidates = GatewayProcessManager.BuildGatewayPathCandidates(GatewayDll);

        candidates.ShouldContain(Path.GetFullPath(GatewayDll));
        candidates.ShouldContain(GatewayApphostExe);
    }

    // -------------------------------------------------------------------------------------
    // AC3 (SECURITY, #2369): an unidentified process is NEVER signalled.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task AC3_StopAsync_NeverKillsAForeignProcess()
    {
        var foreign = new FakeProcessHandle(101, @"C:\Program Files\Foreign\important.exe");
        var manager = NewManager(foreign);

        var result = await manager.StopAsync(_home, GatewayDll, CancellationToken.None);

        foreign.KillCount.ShouldBe(0, "a foreign executable path is not the gateway and must never be signalled");
        result.Outcome.ShouldBe(GatewayStopOutcome.NotRunning);
    }

    [Fact]
    public async Task AC3_StopAsync_NeverKillsAProcessWhoseExecutablePathIsNull()
    {
        var unknown = new FakeProcessHandle(102, null);
        var manager = NewManager(unknown);

        var result = await manager.StopAsync(_home, GatewayDll, CancellationToken.None);

        unknown.KillCount.ShouldBe(0, "an unreadable image path means unidentifiable, never 'assume gateway'");
        result.Outcome.ShouldBe(GatewayStopOutcome.NotRunning);
    }

    [Fact]
    public async Task AC3_StopAsync_NeverKillsAProcessWhoseExecutablePathThrows()
    {
        var denied = new FakeProcessHandle(103, null, throwOnPath: true);
        var manager = NewManager(denied);

        var result = await manager.StopAsync(_home, GatewayDll, CancellationToken.None);

        denied.KillCount.ShouldBe(0, "access-denied on the module path must skip the process, not select it");
        result.Outcome.ShouldBe(GatewayStopOutcome.NotRunning);
    }

    [Fact]
    public async Task AC3_StopAsync_SkipsUnidentifiableProcessesAndStillFindsTheRealGateway()
    {
        var denied = new FakeProcessHandle(1, null, throwOnPath: true);
        var nullPath = new FakeProcessHandle(2, null);
        var foreign = new FakeProcessHandle(3, @"C:\Program Files\Foreign\important.exe");
        var gateway = new FakeProcessHandle(4, GatewayDll);
        var manager = NewManager(denied, nullPath, foreign, gateway);

        var result = await manager.StopAsync(_home, GatewayDll, CancellationToken.None);

        gateway.KillCount.ShouldBe(1);
        denied.KillCount.ShouldBe(0);
        nullPath.KillCount.ShouldBe(0);
        foreign.KillCount.ShouldBe(0);
        result.Outcome.ShouldBe(GatewayStopOutcome.Stopped);
    }

    [Fact]
    public async Task AC3_StopAsync_DoesNotEnumerateOrKillAnything_WhenNoGatewayBinaryPathIsSupplied()
    {
        var gateway = new FakeProcessHandle(5, GatewayDll);
        var manager = NewManager(gateway);

        var result = await manager.StopAsync(_home, gatewayBinaryPath: null, CancellationToken.None);

        gateway.KillCount.ShouldBe(0, "with no expected path there is nothing to positively identify against");
        result.Outcome.ShouldBe(GatewayStopOutcome.NotRunning);
    }

    // -------------------------------------------------------------------------------------
    // IsRunning shares the same discovery, so the update skip path cannot disagree with stop.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void AC1_IsRunning_IsTrue_WhenGatewayIsDiscoverableByPathWithoutAPidFile()
    {
        var manager = NewManager(new FakeProcessHandle(9, GatewayDll));

        manager.IsRunning(_home, GatewayDll).ShouldBeTrue();
    }

    [Fact]
    public void AC1_IsRunning_IsFalse_WhenOnlyForeignProcessesAreAlive()
    {
        var manager = NewManager(new FakeProcessHandle(9, @"C:\other\foreign.exe"));

        manager.IsRunning(_home, GatewayDll).ShouldBeFalse();
    }
}

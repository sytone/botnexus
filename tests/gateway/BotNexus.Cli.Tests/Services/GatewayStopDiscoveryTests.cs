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

        public int GracefulStopCount { get; private set; }

        /// <summary>Whether this platform/process offers a graceful signal at all.</summary>
        public bool GracefulStopSupported { get; init; } = true;

        /// <summary>Whether the process actually exits when asked. False models a wedged gateway.</summary>
        public bool ExitsWhenAsked { get; init; } = true;

        /// <summary>The timeout the caller was willing to wait, recorded so it can be asserted.</summary>
        public int? WaitedMilliseconds { get; private set; }

        public string? ExecutablePath =>
            throwOnPath
                ? throw new InvalidOperationException("access denied reading main module")
                : executablePath;

        public bool RequestGracefulStop()
        {
            if (!GracefulStopSupported)
                return false;

            GracefulStopCount++;
            return true;
        }

        public void Kill() => KillCount++;

        /// <summary>
        /// True when this process was selected as the stop target, by either route.
        ///
        /// <remarks>
        /// The discovery tests care that the right process was CHOSEN; whether it was asked
        /// politely or killed is the subject of the clause-5 tests below. Asserting
        /// <see cref="KillCount"/> for "was it selected" coupled every discovery test to the
        /// termination mechanism, and all four broke the moment the mechanism changed.
        /// </remarks>
        /// </summary>
        public bool WasSignalled => GracefulStopCount > 0 || KillCount > 0;

        public bool WaitForExit(int milliseconds)
        {
            WaitedMilliseconds = milliseconds;

            // Only the graceful wait can time out here. Once Kill has been issued the process is
            // gone, which is what the post-kill wait in the manager is checking for.
            return KillCount > 0 || ExitsWhenAsked;
        }
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
        gateway.WasSignalled.ShouldBeTrue();
    }

    // -------------------------------------------------------------------------------------
    // #101 clause 5: ask before killing, and escalate when asked is ignored.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task StopAsync_AsksTheGatewayToShutDown_BeforeKillingIt()
    {
        var gateway = new FakeProcessHandle(4242, GatewayDll);

        var result = await NewManager(gateway).StopAsync(_home, GatewayDll, CancellationToken.None);

        gateway.GracefulStopCount.ShouldBe(1);
        gateway.KillCount.ShouldBe(0,
            "a gateway that exits when asked is never killed - SIGKILL skips the shutdown path, " +
            "which is where the TRUNCATE WAL checkpoint happens");
        result.Outcome.ShouldBe(GatewayStopOutcome.Stopped);
    }

    [Fact]
    public async Task StopAsync_WaitsTheStatedBudget_ForAGracefulExit()
    {
        var gateway = new FakeProcessHandle(4242, GatewayDll);

        await NewManager(gateway).StopAsync(_home, GatewayDll, CancellationToken.None);

        gateway.WaitedMilliseconds.ShouldBe(
            (int)GatewayProcessManager.GracefulStopTimeout.TotalMilliseconds);
    }

    [Fact]
    public async Task StopAsync_KillsAWedgedGateway_AndSaysHowLongItWaited()
    {
        // Escalation is not optional: the caller is usually a redeploy about to overwrite
        // extension assemblies this process still holds mapped.
        var wedged = new FakeProcessHandle(4242, GatewayDll) { ExitsWhenAsked = false };

        var result = await NewManager(wedged).StopAsync(_home, GatewayDll, CancellationToken.None);

        wedged.GracefulStopCount.ShouldBe(1);
        wedged.KillCount.ShouldBe(1);
        result.Outcome.ShouldBe(GatewayStopOutcome.Stopped);
        var message = result.Message.ShouldNotBeNull();
        message.ShouldContain(
            $"{GatewayProcessManager.GracefulStopTimeout.TotalSeconds:0}s",
            Case.Sensitive,
            "'did not stop' invites a guess at how long anyone waited");
    }

    [Fact]
    public async Task StopAsync_KillsImmediately_WhenThePlatformHasNoGracefulSignal()
    {
        // Windows has no SIGTERM. Waiting out a budget for a request that was never delivered
        // would add ten seconds to every stop and change nothing.
        var windowsLike = new FakeProcessHandle(4242, GatewayDll) { GracefulStopSupported = false };

        var result = await NewManager(windowsLike).StopAsync(_home, GatewayDll, CancellationToken.None);

        windowsLike.GracefulStopCount.ShouldBe(0);
        windowsLike.KillCount.ShouldBe(1);
        // Non-null receiver on purpose: ShouldNotContain on a nullable string binds the
        // IEnumerable<char> overload and compares character-wise, which passes for the wrong reason.
        var message = result.Message.ShouldNotBeNull();
        message.ShouldNotContain("ignored a shutdown request", Case.Sensitive,
            "nothing was ignored - nothing was asked");
        result.Outcome.ShouldBe(GatewayStopOutcome.Stopped);
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

        gateway.WasSignalled.ShouldBeTrue("the live gateway must be discovered without a PID file (#2772)");
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

        apphost.WasSignalled.ShouldBeTrue("the apphost beside the DLL is the same gateway binary");
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

        foreign.WasSignalled.ShouldBeFalse("a foreign executable path is not the gateway and must never be signalled");
        result.Outcome.ShouldBe(GatewayStopOutcome.NotRunning);
    }

    [Fact]
    public async Task AC3_StopAsync_NeverKillsAProcessWhoseExecutablePathIsNull()
    {
        var unknown = new FakeProcessHandle(102, null);
        var manager = NewManager(unknown);

        var result = await manager.StopAsync(_home, GatewayDll, CancellationToken.None);

        unknown.WasSignalled.ShouldBeFalse("an unreadable image path means unidentifiable, never 'assume gateway'");
        result.Outcome.ShouldBe(GatewayStopOutcome.NotRunning);
    }

    [Fact]
    public async Task AC3_StopAsync_NeverKillsAProcessWhoseExecutablePathThrows()
    {
        var denied = new FakeProcessHandle(103, null, throwOnPath: true);
        var manager = NewManager(denied);

        var result = await manager.StopAsync(_home, GatewayDll, CancellationToken.None);

        denied.WasSignalled.ShouldBeFalse("access-denied on the module path must skip the process, not select it");
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

        gateway.WasSignalled.ShouldBeTrue();
        denied.WasSignalled.ShouldBeFalse();
        nullPath.WasSignalled.ShouldBeFalse();
        foreign.WasSignalled.ShouldBeFalse();
        result.Outcome.ShouldBe(GatewayStopOutcome.Stopped);
    }

    [Fact]
    public async Task AC3_StopAsync_DoesNotEnumerateOrKillAnything_WhenNoGatewayBinaryPathIsSupplied()
    {
        var gateway = new FakeProcessHandle(5, GatewayDll);
        var manager = NewManager(gateway);

        var result = await manager.StopAsync(_home, gatewayBinaryPath: null, CancellationToken.None);

        gateway.WasSignalled.ShouldBeFalse("with no expected path there is nothing to positively identify against");
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

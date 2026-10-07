using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Pins every repository-owned gateway termination surface to the API-first shutdown owner.
/// Native process and service controls are escalation details of GatewayProcessManager, never
/// alternative command-level lifecycle policies (#4721).
/// </summary>
public sealed class GatewayPlannedShutdownSurfaceArchitectureTests : ArchitectureTest
{
    private const string CliRoot = "src/gateway/BotNexus.Cli";
    private const string GatewayCommand = CliRoot + "/Commands/GatewayCommand.cs";
    private const string ServeCommand = CliRoot + "/Commands/ServeCommand.cs";
    private const string UpdateCommand = CliRoot + "/Commands/UpdateCommand.cs";
    private const string ProcessManager = CliRoot + "/Services/GatewayProcessManager.cs";
    private const string ProcessManagerContract = CliRoot + "/Services/IGatewayProcessManager.cs";
    private const string ProcessHandle = CliRoot + "/Services/GatewayProcessHandle.cs";
    private const string ForegroundLifecycle = CliRoot + "/Services/ForegroundGatewayLifecycle.cs";
    private const string ServiceLifecycle = CliRoot + "/Services/GatewayServiceLifecycle.cs";
    private const string SyncScript = "scripts/botnexus-sync.sh";

    private static readonly string[] ServiceManagers =
    [
        CliRoot + "/Services/SystemdServiceManager.cs",
        CliRoot + "/Services/WindowsServiceManager.cs",
        CliRoot + "/Services/LaunchdServiceManager.cs",
    ];

    private static readonly Regex StopAsyncCall = new(
        @"\b_?processManager\s*\.\s*StopAsync\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RawServiceTermination = new(
        @"\bsystemctl\b[^\r\n]*\bstop\b|\bsc(?:\.exe)?\b[^\r\n]*\bstop\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RawGatewayKill = new(
        @"(?m)^\s*(?!#)(?:kill\s+(?!-0\b)|[^\r\n]*\bkill\s+-9\b|Stop-Process\b|taskkill\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void StopRestartAndUpdate_RouteThroughApiFirstProcessManager()
    {
        var gateway = Read(GatewayCommand);
        var stop = Slice(gateway, "private async Task<int> StopAsync", "private async Task<int> StatusAsync");
        var restart = Slice(gateway, "private async Task<int> RestartAsync", "private static string FormatUptime");
        var update = Slice(Read(UpdateCommand), "internal async Task<int> ExecuteAsync", "protected virtual async Task<int> RunGitPullStepAsync");

        AssertSurfaceRoutesThroughStopAsync(stop, "gateway stop", "GatewayProbeUrlResolver.ResolveFromHome");
        AssertSurfaceRoutesThroughStopAsync(restart, "gateway restart", "GatewayProbeUrlResolver.ResolveFromHome");
        AssertSurfaceRoutesThroughStopAsync(update, "update", "Stopping gateway");
    }

    [Fact]
    public void ServiceInstall_PrefersNativeApphostWhenPresent()
    {
        var install = Slice(Read(GatewayCommand), "private static async Task<int> InstallServiceAsync", "private async Task<int> UninstallServiceAsync");
        var resolve = install.IndexOf("GatewayProcessManager.ResolveApphostPath", StringComparison.Ordinal);
        var installCall = install.IndexOf("manager.InstallAsync", StringComparison.Ordinal);
        resolve.ShouldBeGreaterThanOrEqualTo(0);
        installCall.ShouldBeGreaterThan(resolve,
            "service installation must resolve and pass the native apphost before falling back to the DLL");
    }

    [Fact]
    public void Uninstall_UsesDedicatedServiceLifecycleBeforeDefinitionRemoval()
    {
        var command = SliceFrom(Read(GatewayCommand), "private async Task<int> UninstallServiceAsync");
        command.ShouldContain("GatewayServiceLifecycle");
        command.ShouldContain("StopAndRemoveAsync");

        var lifecycle = Read(ServiceLifecycle);
        var request = lifecycle.IndexOf("RequestPlannedShutdownAsync", StringComparison.Ordinal);
        var fallback = lifecycle.IndexOf("serviceManager.StopAsync", StringComparison.Ordinal);
        var uninstall = lifecycle.IndexOf("serviceManager.UninstallAsync", request, StringComparison.Ordinal);
        request.ShouldBeGreaterThanOrEqualTo(0,
            "service uninstall must request authenticated planned shutdown without requiring a process handle");
        fallback.ShouldBeGreaterThan(request,
            "native service stop is only a fallback after planned shutdown and bounded observation");
        uninstall.ShouldBeGreaterThan(fallback,
            "definition deletion must remain after the native fallback and confirmed-down check");
        lifecycle.ShouldContain("IsRunningAsync");
    }

    [Fact]
    public void AttachedStartCancellation_RoutesChildThroughPlannedShutdownOwner()
    {
        var body = Slice(Read(GatewayCommand), "StartAttachedAsync", "internal static string ResolveGatewayBinaryPath");
        AssertForegroundGatewayUsesSharedLifecycle(body, "gateway start --attached");
    }

    [Fact]
    public void ServeGatewayCancellation_RoutesChildThroughPlannedShutdownOwner()
    {
        var body = Slice(Read(ServeCommand), "ServeGatewayAsync", "private static async Task<int> ServeProbeAsync");
        AssertForegroundGatewayUsesSharedLifecycle(body, "serve gateway");
    }

    [Fact]
    public void SyncScript_UsesSupportedGatewayStop_AndContainsNoRawKillPath()
    {
        var source = Read(SyncScript);

        Regex.IsMatch(source, @"\bgateway\s+stop\b", RegexOptions.CultureInvariant).ShouldBeTrue(
            "the shipped sync script must stop through the BotNexus CLI so authentication, planned intent, " +
            "bounded waiting, identity checks, and escalation stay owned by GatewayProcessManager");
        RawGatewayKill.Matches(source).Select(match => match.Value.Trim()).ShouldBeEmpty(
            "the shipped sync script must not maintain a parallel kill/kill -9 gateway shutdown path");
    }

    [Fact]
    public void GatewayTerminationPrimitives_AreOwnedOnlyByGatewayProcessManager()
    {
        var offenders = new[] { GatewayCommand, ServeCommand }.Where(relative => Regex.IsMatch(
                Read(relative),
                @"\.\s*Kill\s*\(|\bStop-Process\b|\btaskkill\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "gateway commands and OS service-definition managers must not signal a process/service directly. " +
            "Route intentional termination through IGatewayProcessManager.StopAsync; native stop/kill is bounded " +
            "escalation owned by GatewayProcessManager. Offenders: " + string.Join(", ", offenders));

        RawServiceTermination.IsMatch(Read(ProcessManager)).ShouldBeFalse(
            "GatewayProcessManager owns process escalation, while native service fallback belongs to the service lifecycle abstraction");
        RawServiceTermination.IsMatch(Read(ServiceLifecycle)).ShouldBeFalse(
            "the service lifecycle must call typed service abstractions rather than shelling out directly");
        Read(ProcessManager).ShouldContain("handle.Kill()");
        Read(ProcessHandle).ShouldContain("process.Kill()");
    }

    [Fact]
    public void ProcessManagerContract_DescribesApiFirstPlannedShutdown()
    {
        var contract = Read(ProcessManagerContract);

        contract.Contains("planned shutdown", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
            "IGatewayProcessManager.StopAsync is the public lifecycle contract and must name planned shutdown");
        contract.ShouldContain("gatewayUrl");
        contract.Contains("escalat", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
            "native signalling must be documented as bounded escalation, not the primary stop path");
    }

    [Fact]
    public void Fence_CoversEveryKnownGatewayTerminationEntryPoint()
    {
        var gateway = Read(GatewayCommand);
        gateway.ShouldContain("new Command(\"stop\"");
        gateway.ShouldContain("new Command(\"restart\"");
        gateway.ShouldContain("new Command(\"uninstall\"");
        gateway.ShouldContain("attachedOption");
        Read(ServeCommand).ShouldContain("ServeGatewayAsync");
        Read(UpdateCommand).ShouldContain("Stopping gateway");
        File.Exists(Resolve(SyncScript)).ShouldBeTrue();
        ServiceManagers.ShouldAllBe(relative => File.Exists(Resolve(relative)));
    }

    private static void AssertSurfaceRoutesThroughStopAsync(string source, string surface, string discoveryPin)
    {
        source.ShouldContain(discoveryPin);
        StopAsyncCall.IsMatch(source).ShouldBeTrue(
            $"{surface} must route gateway termination through IGatewayProcessManager.StopAsync");
    }

    private void AssertForegroundGatewayUsesSharedLifecycle(string body, string surface)
    {
        body.ShouldContain("ForegroundGatewayLifecycle.WaitForExitAsync");
        var lifecycle = Read(ForegroundLifecycle);
        StopAsyncCall.IsMatch(lifecycle).ShouldBeTrue(
            $"{surface} must delegate cancellation to shared API-first shutdown policy");
        lifecycle.ShouldContain("new CancellationTokenSource(CleanupTimeout)");
        lifecycle.ShouldContain("GatewayPidFile.Capture(process)");
        lifecycle.ShouldContain("await process.WaitForExitAsync(cleanup.Token)");
    }

    private string Read(string relative)
    {
        var path = Resolve(relative);
        File.Exists(path).ShouldBeTrue($"Expected shutdown surface source not found: {relative}");
        return File.ReadAllText(path);
    }

    private string Resolve(string relative) =>
        Path.Combine(Repository.Root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string SliceFrom(string source, string startMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Expected source marker was not found: {startMarker}");
        return source[start..];
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Expected source marker was not found: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"Expected source marker was not found after {startMarker}: {endMarker}");
        return source[start..end];
    }
}

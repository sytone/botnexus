using BotNexus.Cli.Services;
using NSubstitute;

namespace BotNexus.Cli.Tests;

public sealed class ServiceLifecycleTests
{
    public static IEnumerable<object[]> Managers()
    {
        yield return ["systemd", "systemctl"];
        yield return ["windows", "sc.exe"];
        yield return ["launchd", "launchctl"];
    }

    [Theory]
    [MemberData(nameof(Managers))]
    public async Task StopAndRemove_ApiFirstThenNativeFallbackThenDelete(
        string managerKind,
        string nativeCommand)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bn-service-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, "definition");
        try
        {
            var events = new List<string>();
            var runner = new FakeRunner(events);
            var manager = CreateManager(managerKind, runner, path);
            var process = Substitute.For<IGatewayProcessManager>();
            process.RequestPlannedShutdownAsync("home", "http://gateway", Arg.Any<CancellationToken>())
                .Returns(_ => { events.Add("api"); return true; });
            process.StopAsync("home", "gateway.dll", Arg.Any<CancellationToken>(), "http://gateway")
                .Returns(_ => { events.Add("process"); return new GatewayStopResult(true, "no handle", GatewayStopOutcome.NotRunning); });
            var lifecycle = new GatewayServiceLifecycle(
                process,
                (_, cancellationToken) => throw new OperationCanceledException(cancellationToken),
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero);

            var result = await lifecycle.StopAndRemoveAsync(manager, "home", "gateway.dll", "http://gateway", CancellationToken.None);

            result.Success.ShouldBeTrue();
            events.ShouldContain("api");
            events.ShouldContain("process");
            events.ShouldContain($"native:{nativeCommand}");
            events.IndexOf("api").ShouldBeLessThan(events.IndexOf($"native:{nativeCommand}"));
            if (managerKind == "launchd")
                File.Exists(path).ShouldBeFalse();
            else
            {
                events.ShouldContain("delete");
                events.IndexOf($"native:{nativeCommand}").ShouldBeLessThan(events.IndexOf("delete"));
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(Managers))]
    public async Task StopAndRemove_NativeFallbackDoesNotExit_NeverDeletes(
        string managerKind,
        string nativeCommand)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bn-service-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, "definition");
        try
        {
            var events = new List<string>();
            var runner = new FakeRunner(events, neverStops: true);
            var manager = CreateManager(managerKind, runner, path);
            var process = Substitute.For<IGatewayProcessManager>();
            process.RequestPlannedShutdownAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
            process.StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
                .Returns(new GatewayStopResult(true, "no handle", GatewayStopOutcome.NotRunning));
            var lifecycle = new GatewayServiceLifecycle(
                process,
                (_, cancellationToken) => throw new OperationCanceledException(cancellationToken),
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero);

            var result = await lifecycle.StopAndRemoveAsync(manager, "home", "gateway.dll", "http://gateway", CancellationToken.None);

            result.Success.ShouldBeFalse();
            events.ShouldContain($"native:{nativeCommand}");
            events.ShouldNotContain("delete");
            File.Exists(path).ShouldBeTrue();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static IOsServiceManager CreateManager(string managerKind, FakeRunner runner, string path) => managerKind switch
    {
        "systemd" => new SystemdServiceManager(runner, path),
        "windows" => new WindowsServiceManager(runner),
        "launchd" => new LaunchdServiceManager(runner, path),
        _ => throw new ArgumentOutOfRangeException(nameof(managerKind))
    };

    private sealed class FakeRunner(List<string> events, bool neverStops = false) : IServiceProcessRunner
    {
        private bool _nativeStop;

        public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
            => Run(fileName, string.Join(' ', arguments));

        public Task<ProcessRunResult> RunRawAsync(string fileName, string argumentLine, CancellationToken cancellationToken)
            => Run(fileName, argumentLine);

        private Task<ProcessRunResult> Run(string fileName, string arguments)
        {
            if (arguments.Contains("stop", StringComparison.OrdinalIgnoreCase) || arguments.Contains("unload", StringComparison.OrdinalIgnoreCase))
            {
                _nativeStop = true;
                events.Add($"native:{fileName}");
                return Task.FromResult(new ProcessRunResult(0, string.Empty));
            }

            if (arguments.Contains("delete", StringComparison.OrdinalIgnoreCase) || arguments.Contains("disable", StringComparison.OrdinalIgnoreCase) || arguments.Contains("daemon-reload", StringComparison.OrdinalIgnoreCase))
                events.Add("delete");

            if (arguments.Contains("is-active", StringComparison.OrdinalIgnoreCase) || arguments.Contains("print", StringComparison.OrdinalIgnoreCase) || arguments.Contains("list", StringComparison.OrdinalIgnoreCase))
            {
                var running = neverStops || !_nativeStop;
                return Task.FromResult(new ProcessRunResult(running ? 0 : 1, running ? "RUNNING" : "STOPPED"));
            }

            if (arguments.Contains("query", StringComparison.OrdinalIgnoreCase))
            {
                var running = neverStops || !_nativeStop;
                return Task.FromResult(new ProcessRunResult(0, running ? "STATE RUNNING" : "STATE STOPPED"));
            }

            return Task.FromResult(new ProcessRunResult(0, string.Empty));
        }
    }
}

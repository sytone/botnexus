using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Services;
using NSubstitute;
using Spectre.Console;

namespace BotNexus.Cli.Tests.Commands;

[Collection("AnsiConsole")]
public class UpdateCommandRecoveryTests
{
    private static readonly string Repo = Path.Combine(Path.GetTempPath(), "recovery-repo");
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "recovery-home");

    private static MockFileSystem Artifacts()
    {
        var fs = new MockFileSystem();
        var dll = UpdateCommand.ResolveGatewayBinaryPath(Repo);
        var root = Path.GetDirectoryName(dll) ?? throw new InvalidOperationException();
        fs.AddFile(dll, new MockFileData("prior gateway"));
        fs.AddFile(Path.Combine(root, "BotNexus.Gateway.Api.runtimeconfig.json"), new MockFileData("{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"frameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"},{\"name\":\"Microsoft.AspNetCore.App\",\"version\":\"10.0.0\"}]}}"));
        fs.AddFile(Path.Combine(root, "BotNexus.Gateway.Api.deps.json"), new MockFileData("{\"targets\":{\"net10.0\":{\"Gateway/1\":{\"runtime\":{\"BotNexus.Gateway.Api.dll\":{},\"Dependency.dll\":{}}}}}}"));
        fs.AddFile(Path.Combine(root, "Dependency.dll"), new MockFileData("prior dependency"));
        fs.AddFile(Path.Combine(root, "wwwroot", "index.html"), new MockFileData("prior content"));
        fs.AddFile(Path.Combine(root, OperatingSystem.IsWindows() ? "BotNexus.Gateway.Api.exe" : "BotNexus.Gateway.Api"), new MockFileData("prior apphost"));
        return fs;
    }

    private sealed class FailedBuildCommand(IGatewayProcessManager manager, MockFileSystem fs, int result = 17)
        : UpdateCommand(manager, fs)
    {
        internal Action? DuringBuild { get; set; }
        internal bool UseActualBuildStep { get; set; }
        internal int CandidateRestarts { get; private set; }
        protected override Task<int> RunGitPullStepAsync(string repoRoot, bool verbose, CancellationToken cancellationToken) => Task.FromResult(0);
        protected override Task<bool> CanSkipRebuildAsync(string repoRoot, CancellationToken cancellationToken) => Task.FromResult(false);
        protected override Task<bool> WaitForPortFreeAsync(int port, CancellationToken cancellationToken) => Task.FromResult(true);
        protected override Task<int> RunBuildAndDeployAsync(string repoRoot, string home, bool verbose, CancellationToken cancellationToken)
        {
            if (UseActualBuildStep)
                return base.RunBuildAndDeployAsync(repoRoot, home, verbose, cancellationToken);
            fs.File.WriteAllText(ResolveGatewayBinaryPath(repoRoot), "partial candidate");
            DuringBuild?.Invoke();
            return Task.FromResult(result);
        }
        protected override Task<int> RunGatewayBuildAsync(string repoRoot, bool verbose, CancellationToken cancellationToken)
        {
            fs.File.WriteAllText(ResolveGatewayBinaryPath(repoRoot), "partial candidate");
            return Task.FromResult(result);
        }
        protected override Task<int> RunRestartAsync(string home, int port, string repoRoot, CancellationToken cancellationToken)
        {
            CandidateRestarts++;
            return Task.FromResult(0);
        }
    }

    private static IGatewayProcessManager Manager(bool success = true, GatewayStopOutcome outcome = GatewayStopOutcome.Stopped)
    {
        var manager = Substitute.For<IGatewayProcessManager>();
        manager.StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(StoppedResult(success, outcome, UpdateCommand.ResolveGatewayBinaryPath(Repo)));
        manager.StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStartResult(true, 123, "healthy"));
        return manager;
    }

    private static GatewayStopResult StoppedResult(bool success, GatewayStopOutcome outcome, string? binary)
    {
        return new GatewayStopResult(success, "observed", outcome, binary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("deployment-A")]
    [InlineData("relative")]
    public async Task FailedBuild_UnverifiedOrDifferentStoppedDeployment_DoesNotRecover(string? stoppedDeployment)
    {
        var manager = Manager();
        var binary = stoppedDeployment switch
        {
            null => null,
            "relative" => "BotNexus.Gateway.Api.dll",
            _ => ResolveOtherBinary(stoppedDeployment)
        };
        manager.StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(StoppedResult(true, GatewayStopOutcome.Stopped, binary));
        var fs = Artifacts();
        (await new FailedBuildCommand(manager, fs).ExecuteAsync(Repo, Home, 54321, false, CancellationToken.None)).ShouldBe(17);
        await manager.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
        fs.AllDirectories.ShouldNotContain(path => path.Contains(".botnexus-update-recovery-", StringComparison.Ordinal));
    }

    private static string ResolveOtherBinary(string deployment) => UpdateCommand.ResolveGatewayBinaryPath(Path.Combine(Path.GetTempPath(), deployment));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"runtimeOptions\":{}}")]
    [InlineData("{\"runtimeOptions\":{\"tfm\":\"net10.0\"}}")]
    [InlineData("{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"frameworks\":[]}}")]
    [InlineData("{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\"}}}")]
    public async Task FailedBuild_InvalidPriorRuntimeConfig_DoesNotRecover(string config)
    {
        var fs = Artifacts();
        var root = Path.GetDirectoryName(UpdateCommand.ResolveGatewayBinaryPath(Repo)) ?? throw new InvalidOperationException();
        fs.File.WriteAllText(Path.Combine(root, "BotNexus.Gateway.Api.runtimeconfig.json"), config);
        var manager = Manager();
        (await new FailedBuildCommand(manager, fs).ExecuteAsync(Repo, Home, 54321, false, CancellationToken.None)).ShouldBe(17);
        await manager.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Snapshot_CancelledRestore_DoesNotDeleteCandidateAndRetainsPreservation()
    {
        var fs = Artifacts();
        var dll = UpdateCommand.ResolveGatewayBinaryPath(Repo);
        using var snapshot = GatewayBuildSnapshot.Capture(fs, dll);
        fs.File.WriteAllText(dll, "candidate remains untouched");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => snapshot.RestoreAsync(cancellation.Token));
        fs.File.ReadAllText(dll).ShouldBe("candidate remains untouched");
        snapshot.Dispose();
        fs.Directory.Exists(snapshot.PreservedDirectory).ShouldBeTrue();
    }

    [Theory]
    [InlineData("framework")]
    [InlineData("frameworks")]
    [InlineData("includedFrameworks")]
    public async Task Snapshot_ValidRuntimeFrameworkLayouts_Restore(string property)
    {
        var fs = Artifacts();
        var dll = UpdateCommand.ResolveGatewayBinaryPath(Repo);
        var root = Path.GetDirectoryName(dll) ?? throw new InvalidOperationException();
        const string framework = "{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}";
        var value = property == "framework" ? framework : "[" + framework + "]";
        fs.File.WriteAllText(Path.Combine(root, "BotNexus.Gateway.Api.runtimeconfig.json"),
            "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"" + property + "\":" + value + "}}");
        using var snapshot = GatewayBuildSnapshot.Capture(fs, dll);
        fs.File.WriteAllText(dll, "candidate");
        await snapshot.RestoreAsync(CancellationToken.None);
        fs.File.ReadAllText(dll).ShouldBe("prior gateway");
    }

    [Fact]
    public async Task FailedBuild_MatchingApphostIdentity_AuthorizesRecovery()
    {
        var manager = Manager();
        var dll = UpdateCommand.ResolveGatewayBinaryPath(Repo);
        var apphost = Path.Combine(Path.GetDirectoryName(dll) ?? throw new InvalidOperationException(),
            OperatingSystem.IsWindows() ? "BotNexus.Gateway.Api.exe" : "BotNexus.Gateway.Api");
        manager.StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(StoppedResult(true, GatewayStopOutcome.Stopped, apphost));
        (await new FailedBuildCommand(manager, Artifacts()).ExecuteAsync(Repo, Home, 54321, false, CancellationToken.None)).ShouldBe(17);
        await manager.Received(1).StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_CancellationDuringInventoryOrCopy_StopsAndRetainsBackup(bool duringCopy)
    {
        var fs = Artifacts();
        using var cancellation = new CancellationTokenSource();
        var files = Substitute.For<IFile>();
        var proxy = Substitute.For<IFileSystem>();
        proxy.File.Returns(files);
        proxy.Directory.Returns(fs.Directory);
        proxy.Path.Returns(fs.Path);
        files.Exists(Arg.Any<string>()).Returns(call => fs.File.Exists(call.Arg<string>()));
        files.ReadAllText(Arg.Any<string>()).Returns(call => fs.File.ReadAllText(call.Arg<string>()));
        files.GetAttributes(Arg.Any<string>()).Returns(call => fs.File.GetAttributes(call.Arg<string>()));
        files.When(file => file.Copy(Arg.Any<string>(), Arg.Any<string>())).Do(call => fs.File.Copy(call.ArgAt<string>(0), call.ArgAt<string>(1)));
        if (!OperatingSystem.IsWindows())
        {
            files.GetUnixFileMode(Arg.Any<string>()).Returns(call =>
                !OperatingSystem.IsWindows() ? fs.File.GetUnixFileMode(call.Arg<string>()) : throw new PlatformNotSupportedException());
            files.When(file =>
            {
                if (!OperatingSystem.IsWindows()) file.SetUnixFileMode(Arg.Any<string>(), Arg.Any<UnixFileMode>());
            }).Do(call =>
            {
                if (!OperatingSystem.IsWindows()) fs.File.SetUnixFileMode(call.Arg<string>(), call.Arg<UnixFileMode>());
            });
        }
        var restoring = false;
        files.OpenRead(Arg.Any<string>()).Returns(call =>
        {
            if (restoring && !duringCopy) cancellation.Cancel();
            return fs.File.OpenRead(call.Arg<string>());
        });
        files.Open(Arg.Any<string>(), FileMode.CreateNew, FileAccess.Write).Returns(call =>
        {
            cancellation.Cancel(); // Explicit boundary: destination opened, copy has not begun.
            return fs.File.Open(call.Arg<string>(), FileMode.CreateNew, FileAccess.Write);
        });
        var dll = UpdateCommand.ResolveGatewayBinaryPath(Repo);
        using var snapshot = GatewayBuildSnapshot.Capture(proxy, dll);
        fs.File.WriteAllText(dll, "partial candidate");
        restoring = true;
        await Should.ThrowAsync<OperationCanceledException>(() => snapshot.RestoreAsync(cancellation.Token));
        snapshot.Dispose();
        fs.Directory.Exists(snapshot.PreservedDirectory).ShouldBeTrue();
        fs.File.ReadAllText(Path.Combine(snapshot.PreservedDirectory, "BotNexus.Gateway.Api.dll")).ShouldBe("prior gateway");
        if (!duringCopy) fs.File.ReadAllText(dll).ShouldBe("partial candidate");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedBuild_RestoresWholePriorTreeAndStartsExactlyOnce_PreservingFailure(bool startSucceeds)
    {
        var fs = Artifacts();
        var manager = Manager();
        manager.StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var options = call.Arg<GatewayStartOptions>();
            options.ExecutablePath.ShouldBe(UpdateCommand.ResolveGatewayBinaryPath(Repo));
            options.HomePath.ShouldBe(Home);
            var arguments = options.Arguments ?? throw new InvalidOperationException("Recovery arguments missing");
            arguments.ShouldContain("http://localhost:54321");
            options.HealthUrl.ShouldBe("http://localhost:54321/health");
            options.ReadinessTimeout.ShouldBe(TimeSpan.FromSeconds(60));
            call.Arg<CancellationToken>().CanBeCanceled.ShouldBeTrue();
            fs.File.ReadAllText(options.ExecutablePath).ShouldBe("prior gateway");
            var root = Path.GetDirectoryName(options.ExecutablePath) ?? throw new InvalidOperationException();
            arguments.ShouldContain($"--contentRoot \"{root}\"");
            fs.File.ReadAllText(Path.Combine(root, "wwwroot", "index.html")).ShouldBe("prior content");
            fs.File.ReadAllText(Path.Combine(root, "Dependency.dll")).ShouldBe("prior dependency");
            fs.File.ReadAllText(Path.Combine(root, OperatingSystem.IsWindows() ? "BotNexus.Gateway.Api.exe" : "BotNexus.Gateway.Api")).ShouldBe("prior apphost");
            fs.File.Exists(Path.Combine(root, "candidate-only.dll")).ShouldBeFalse();
            return new GatewayStartResult(startSucceeds, startSucceeds ? 123 : null, "result");
        });
        var command = new FailedBuildCommand(manager, fs);
        command.DuringBuild = () => fs.File.WriteAllText(Path.Combine(Path.GetDirectoryName(UpdateCommand.ResolveGatewayBinaryPath(Repo)) ?? throw new InvalidOperationException(), "candidate-only.dll"), "candidate");
        (await command.ExecuteAsync(Repo, Home, 54321, false, CancellationToken.None)).ShouldBe(17);
        command.CandidateRestarts.ShouldBe(0);
        await manager.Received(1).StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true, GatewayStopOutcome.NotRunning)]
    [InlineData(false, GatewayStopOutcome.Failed)]
    [InlineData(false, GatewayStopOutcome.Stopped)]
    [InlineData(true, GatewayStopOutcome.Failed)]
    public async Task FailedBuild_WithoutConfirmedStop_DoesNotRecover(bool success, GatewayStopOutcome outcome)
    {
        var manager = Manager(success, outcome);
        var command = new FailedBuildCommand(manager, Artifacts());
        (await command.ExecuteAsync(Repo, Home, 54321, false, CancellationToken.None)).ShouldBe(17);
        await manager.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedBuild_IncompletePriorArtifacts_RefusesRecovery()
    {
        var fs = Artifacts();
        fs.File.Delete(Path.Combine(Path.GetDirectoryName(UpdateCommand.ResolveGatewayBinaryPath(Repo)) ?? throw new InvalidOperationException(), "Dependency.dll"));
        var manager = Manager();
        (await new FailedBuildCommand(manager, fs).ExecuteAsync(Repo, Home, 54321, false, CancellationToken.None)).ShouldBe(17);
        await manager.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedBuild_CancellationNeverRecovers(bool tokenCanceled)
    {
        using var cancellation = new CancellationTokenSource();
        var manager = Manager();
        var command = new FailedBuildCommand(manager, Artifacts(), tokenCanceled ? 17 : 130);
        if (tokenCanceled)
            command.DuringBuild = cancellation.Cancel;
        (await command.ExecuteAsync(Repo, Home, 54321, false, cancellation.Token)).ShouldBe(tokenCanceled ? 17 : 130);
        await manager.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_ModifiedOrMissingPreservedFile_RefusesRestore(bool missing)
    {
        var fs = Artifacts();
        using var snapshot = GatewayBuildSnapshot.Capture(fs, UpdateCommand.ResolveGatewayBinaryPath(Repo));
        var preserved = fs.Directory.GetFiles(snapshot.PreservedDirectory, "Dependency.dll", SearchOption.AllDirectories).Single();
        if (missing)
            fs.File.Delete(preserved);
        else
            fs.File.WriteAllText(preserved, "tampered");
        await Should.ThrowAsync<IOException>(() => snapshot.RestoreAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedBuild_UntrustedPreservation_DoesNotStartCandidate(bool missing)
    {
        var fs = Artifacts();
        var manager = Manager();
        var command = new FailedBuildCommand(manager, fs);
        command.DuringBuild = () =>
        {
            var output = Path.GetDirectoryName(UpdateCommand.ResolveGatewayBinaryPath(Repo)) ?? throw new InvalidOperationException();
            var parent = Path.GetDirectoryName(output) ?? throw new InvalidOperationException();
            var preserved = fs.Directory.GetDirectories(parent, ".botnexus-update-recovery-*").Single();
            var dependency = Path.Combine(preserved, "Dependency.dll");
            if (missing) fs.File.Delete(dependency);
            else fs.File.WriteAllText(dependency, "tampered");
        };
        (await command.ExecuteAsync(Repo, Home, 54321, false, CancellationToken.None)).ShouldBe(17);
        command.CandidateRestarts.ShouldBe(0);
        await manager.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("BotNexus.Gateway.Api.dll")]
    [InlineData("BotNexus.Gateway.Api.runtimeconfig.json")]
    [InlineData("BotNexus.Gateway.Api.deps.json")]
    public async Task FailedBuild_MissingPriorLaunchFile_DoesNotRecover(string missing)
    {
        var fs = Artifacts();
        var root = Path.GetDirectoryName(UpdateCommand.ResolveGatewayBinaryPath(Repo)) ?? throw new InvalidOperationException();
        fs.File.Delete(Path.Combine(root, missing));
        var manager = Manager();
        (await new FailedBuildCommand(manager, fs).ExecuteAsync(Repo, Home, 54321, false, CancellationToken.None)).ShouldBe(17);
        await manager.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(UpdateCommand.ExtensionDeploymentFailureExitCode)]
    public async Task SuccessfulBuild_UsesNormalRestart_NotPriorOutput(int result)
    {
        var fs = Artifacts();
        var manager = Manager();
        var command = new FailedBuildCommand(manager, fs, result);
        (await command.ExecuteAsync(Repo, Home, 54321, false, CancellationToken.None)).ShouldBe(result);
        command.CandidateRestarts.ShouldBe(1);
        fs.File.ReadAllText(UpdateCommand.ResolveGatewayBinaryPath(Repo)).ShouldBe("partial candidate");
        await manager.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedBuild_CompilerExitTwo_IsNotMistakenForDeploymentFailure()
    {
        var fs = Artifacts();
        var manager = Manager();
        var command = new FailedBuildCommand(manager, fs, 2) { UseActualBuildStep = true };
        (await command.ExecuteAsync(Repo, Home, 54321, false, CancellationToken.None)).ShouldBe(2);
        command.CandidateRestarts.ShouldBe(0);
        fs.File.ReadAllText(UpdateCommand.ResolveGatewayBinaryPath(Repo)).ShouldBe("prior gateway");
        await manager.Received(1).StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedBuild_MissingPreservationDirectory_DoesNotRecover()
    {
        var fs = Artifacts();
        var manager = Manager();
        var command = new FailedBuildCommand(manager, fs);
        command.DuringBuild = () =>
        {
            var output = Path.GetDirectoryName(UpdateCommand.ResolveGatewayBinaryPath(Repo)) ?? throw new InvalidOperationException();
            var parent = Path.GetDirectoryName(output) ?? throw new InvalidOperationException();
            fs.Directory.Delete(fs.Directory.GetDirectories(parent, ".botnexus-update-recovery-*").Single(), true);
        };
        (await command.ExecuteAsync(Repo, Home, 54321, false, CancellationToken.None)).ShouldBe(17);
        await manager.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"targets\":{}}")]
    [InlineData("{\"targets\":[]}")]
    [InlineData("{\"targets\":{\"net10.0\":{}}}")]
    [InlineData("{\"targets\":{\"net10.0\":{\"Gateway/1\":{\"runtime\":{}}}}}")]
    [InlineData("{\"targets\":{\"net10.0\":{\"Gateway/1\":{\"runtime\":{\"_._\":{}}}}}}")]
    public async Task FailedBuild_InvalidPriorManifest_DoesNotRecover(string manifest)
    {
        var fs = Artifacts();
        var root = Path.GetDirectoryName(UpdateCommand.ResolveGatewayBinaryPath(Repo)) ?? throw new InvalidOperationException();
        fs.File.WriteAllText(Path.Combine(root, "BotNexus.Gateway.Api.deps.json"), manifest);
        var manager = Manager();
        (await new FailedBuildCommand(manager, fs).ExecuteAsync(Repo, Home, 54321, false, CancellationToken.None)).ShouldBe(17);
        await manager.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(UpdateCommand.ExtensionDeploymentFailureExitCode)]
    public async Task Build_CancellationBeforeNormalRestart_DoesNotStart(int buildResult)
    {
        var fs = Artifacts();
        var manager = Manager();
        using var cancellation = new CancellationTokenSource();
        var command = new FailedBuildCommand(manager, fs, buildResult) { DuringBuild = cancellation.Cancel };
        (await command.ExecuteAsync(Repo, Home, 54321, false, cancellation.Token)).ShouldBe(buildResult == 0 ? 130 : buildResult);
        command.CandidateRestarts.ShouldBe(0);
        await manager.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Snapshot_RestoresRidAndCultureContentAndEmptyDirectories()
    {
        var fs = Artifacts();
        var dll = UpdateCommand.ResolveGatewayBinaryPath(Repo);
        var root = Path.GetDirectoryName(dll) ?? throw new InvalidOperationException();
        fs.AddFile(Path.Combine(root, "runtimes", "test-rid", "native", "dependency.native"), new MockFileData("prior native"));
        fs.AddFile(Path.Combine(root, "fr", "Dependency.resources.dll"), new MockFileData("prior resources"));
        fs.File.WriteAllText(Path.Combine(root, "BotNexus.Gateway.Api.deps.json"), "{\"targets\":{\"net10.0\":{\"Gateway/1\":{\"runtime\":{\"BotNexus.Gateway.Api.dll\":{},\"Dependency.dll\":{}},\"resources\":{\"lib/net10.0/fr/Dependency.resources.dll\":{\"locale\":\"fr\"}},\"runtimeTargets\":{\"runtimes/test-rid/native/dependency.native\":{\"rid\":\"test-rid\",\"assetType\":\"native\"}}}}}}");
        fs.Directory.CreateDirectory(Path.Combine(root, "empty"));
        using var snapshot = GatewayBuildSnapshot.Capture(fs, dll);
        fs.Directory.Delete(root, true);
        await snapshot.RestoreAsync(CancellationToken.None);
        fs.File.ReadAllText(Path.Combine(root, "runtimes", "test-rid", "native", "dependency.native")).ShouldBe("prior native");
        fs.File.ReadAllText(Path.Combine(root, "fr", "Dependency.resources.dll")).ShouldBe("prior resources");
        fs.Directory.Exists(Path.Combine(root, "empty")).ShouldBeTrue();
    }

    [Fact]
    public async Task FailedBuild_RecoveryException_PreservesExitAndDiagnosticsWithoutRetry()
    {
        var fs = Artifacts();
        var manager = Manager();
        manager.StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<GatewayStartResult>(new IOException("recovery spawn failed")));
        var original = AnsiConsole.Console;
        using var writer = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Interactive = InteractionSupport.No });
        try
        {
            (await new FailedBuildCommand(manager, fs).ExecuteAsync(Repo, Home, 54321, false, CancellationToken.None)).ShouldBe(17);
            writer.ToString().ShouldContain("exit code 17");
            writer.ToString().ShouldContain("recovery spawn failed");
            System.Text.RegularExpressions.Regex.Replace(writer.ToString(), @"\s+", " ").ShouldNotContain("Preserved output:");
            fs.AllDirectories.ShouldNotContain(path => path.Contains(".botnexus-update-recovery-", StringComparison.Ordinal));
            await manager.Received(1).StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
        }
        finally { AnsiConsole.Console = original; }
    }
}

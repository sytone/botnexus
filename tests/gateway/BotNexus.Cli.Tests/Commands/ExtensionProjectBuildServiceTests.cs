using System.Diagnostics;
using BotNexus.Cli.Commands;

namespace BotNexus.Cli.Tests.Commands;

public sealed class ExtensionProjectBuildServiceTests
{
    [Fact]
    public void ResolveBotNexusRepoRoot_DefaultsToBotNexusUnderUserProfile()
    {
        var profile = Path.Combine(Path.GetTempPath(), "profile with spaces");
        ExtensionProjectBuildService.ResolveBotNexusRepoRoot(null, profile)
            .ShouldBe(Path.GetFullPath(Path.Combine(profile, "botnexus")));
    }

    [Fact]
    public void ResolveBotNexusRepoRoot_UsesAndNormalizesExplicitRoot()
    {
        var explicitRoot = Path.Combine(Path.GetTempPath(), "explicit root", "..");
        ExtensionProjectBuildService.ResolveBotNexusRepoRoot(explicitRoot, "unused")
            .ShouldBe(Path.GetFullPath(explicitRoot));
    }

    [Fact]
    public async Task BuildAfterMainAsync_OnlyLaunchesExtensionAfterSuccessfulMainBuild()
    {
        using var fixture = ExtensionBuildFixture.Create("SourceReference");
        var events = new List<string>();
        var runner = new RecordingExtensionBuildRunner(() => events.Add("extension"));
        var service = new ExtensionProjectBuildService(runner, () => fixture.Root, _ => { });

        var success = await service.BuildAfterMainAsync(
            _ =>
            {
                events.Add("main");
                return Task.FromResult(0);
            },
            fixture.ProjectPath, fixture.StagingDirectory, fixture.BotNexusRepoRoot, CancellationToken.None);

        success.ExitCode.ShouldBe(0);
        events.ShouldBe(["main", "extension"]);

        events.Clear();
        runner.Invocations.Clear();
        var failure = await service.BuildAfterMainAsync(
            _ => Task.FromResult(19),
            fixture.ProjectPath, fixture.StagingDirectory, fixture.BotNexusRepoRoot, CancellationToken.None);

        failure.ExitCode.ShouldBe(19);
        runner.Invocations.ShouldBeEmpty();
        events.ShouldBeEmpty();
    }

    [Fact]
    public void BuildExtensionStartInfo_PreservesPropertyPathsAsSingleArguments()
    {
        using var fixture = ExtensionBuildFixture.Create("SourceReference");

        var startInfo = BuildOutputStreamer.BuildExtensionStartInfo(
            fixture.ProjectPath, fixture.BotNexusRepoRoot, fixture.StagingDirectory);

        startInfo.FileName.ShouldBe("dotnet");
        startInfo.ArgumentList.ShouldContain($"/p:BotNexusRepoRoot={Path.GetFullPath(fixture.BotNexusRepoRoot)}");
        startInfo.ArgumentList.ShouldContain(
            $"/p:OutputPath={Path.GetFullPath(fixture.StagingDirectory)}{Path.DirectorySeparatorChar}");
        startInfo.ArgumentList.ShouldContain("/p:AppendTargetFrameworkToOutputPath=false");
        startInfo.ArgumentList.ShouldContain("/p:AppendRuntimeIdentifierToOutputPath=false");
        startInfo.ArgumentList.ShouldNotContain(argument => argument.Contains('"', StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_AbsentRootReportsExactResolvedPathBeforeProcessLaunch()
    {
        using var fixture = ExtensionBuildFixture.Create("SourceReference");
        var missingRoot = Path.Combine(fixture.Root, "missing BotNexus root");
        var runner = new RecordingExtensionBuildRunner();
        var service = new ExtensionProjectBuildService(runner, () => fixture.Root, _ => { });

        var exception = await Should.ThrowAsync<DirectoryNotFoundException>(() => service.BuildAsync(
            fixture.ProjectPath, fixture.StagingDirectory, missingRoot, CancellationToken.None));

        exception.Message.ShouldContain(Path.GetFullPath(missingRoot));
        runner.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task BuildAsync_AbsentProjectReportsExactResolvedPathBeforeProcessLaunch()
    {
        using var fixture = ExtensionBuildFixture.Create("SourceReference");
        var missingProject = Path.Combine(fixture.Root, "missing", "Extension.csproj");
        var runner = new RecordingExtensionBuildRunner();
        var service = new ExtensionProjectBuildService(runner, () => fixture.Root, _ => { });

        var exception = await Should.ThrowAsync<FileNotFoundException>(() => service.BuildAsync(
            missingProject, fixture.StagingDirectory, fixture.BotNexusRepoRoot, CancellationToken.None));

        exception.Message.ShouldContain(Path.GetFullPath(missingProject));
        runner.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task BuildAsync_PassesRootAndExplicitStagingOutputAndReportsResolvedRoot()
    {
        using var fixture = ExtensionBuildFixture.Create("SourceReference");
        var reports = new List<string>();
        var runner = new RecordingExtensionBuildRunner();
        var service = new ExtensionProjectBuildService(runner, () => "unused", reports.Add);

        var result = await service.BuildAsync(
            fixture.ProjectPath, fixture.StagingDirectory, fixture.BotNexusRepoRoot, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.BotNexusRepoRoot.ShouldBe(Path.GetFullPath(fixture.BotNexusRepoRoot));
        result.StagingDirectory.ShouldBe(Path.GetFullPath(fixture.StagingDirectory));
        reports.ShouldContain(message => message.Contains(result.BotNexusRepoRoot, StringComparison.Ordinal));
        var invocation = runner.Invocations.ShouldHaveSingleItem();
        invocation.ProjectPath.ShouldBe(Path.GetFullPath(fixture.ProjectPath));
        invocation.BotNexusRepoRoot.ShouldBe(result.BotNexusRepoRoot);
        invocation.StagingDirectory.ShouldBe(result.StagingDirectory);
    }

    [Fact]
    public async Task BuildAsync_NeverTargetsOrMutatesLiveDeploymentDirectory()
    {
        using var fixture = ExtensionBuildFixture.Create("SourceReference");
        var liveDeployment = Path.Combine(fixture.Root, "live deployment");
        Directory.CreateDirectory(liveDeployment);
        var sentinel = Path.Combine(liveDeployment, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "do not mutate");
        var runner = new RecordingExtensionBuildRunner();
        var service = new ExtensionProjectBuildService(runner, () => fixture.Root, _ => { });

        await service.BuildAsync(
            fixture.ProjectPath, fixture.StagingDirectory, fixture.BotNexusRepoRoot, CancellationToken.None);

        (await File.ReadAllTextAsync(sentinel)).ShouldBe("do not mutate");
        Directory.GetFiles(liveDeployment).ShouldBe([sentinel]);
        runner.Invocations.ShouldHaveSingleItem().StagingDirectory.ShouldNotBe(liveDeployment);
    }

    [Theory]
    [InlineData("SourceReference", "BotNexus.Domain.Wire.dll")]
    [InlineData("BinaryReference", "BotNexus.Domain.dll")]
    public async Task BuildAsync_OutOfTreeContractFixtureCompilesWithRequiredManagedDependency(
        string fixtureName, string requiredDependency)
    {
        using var fixture = ExtensionBuildFixture.Create(fixtureName);
        if (fixtureName == "BinaryReference")
            await BuildBinaryReferencePrerequisiteAsync(fixture.BotNexusRepoRoot);

        var liveDeployment = Path.Combine(fixture.Root, "live deployment");
        Directory.CreateDirectory(liveDeployment);
        var sentinel = Path.Combine(liveDeployment, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "unchanged");
        var service = new ExtensionProjectBuildService(
            new BuildOutputExtensionProjectRunner(verbose: false), () => fixture.Root, _ => { });

        var result = await service.BuildAsync(
            fixture.ProjectPath, fixture.StagingDirectory, fixture.BotNexusRepoRoot, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        File.Exists(Path.Combine(fixture.StagingDirectory, $"Contract.{fixtureName}.dll")).ShouldBeTrue();
        File.Exists(Path.Combine(fixture.StagingDirectory, requiredDependency)).ShouldBeTrue();
        (await File.ReadAllTextAsync(sentinel)).ShouldBe("unchanged");
        Directory.GetFiles(liveDeployment).ShouldBe([sentinel]);
    }

    private static async Task BuildBinaryReferencePrerequisiteAsync(string repoRoot)
    {
        var project = Path.Combine(repoRoot, "src", "domain", "BotNexus.Domain", "BotNexus.Domain.csproj");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "build", project, "-c", "Release", "--nologo", "--tl:off" })
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start binary-reference prerequisite build.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await standardOutput + await standardError;
        process.ExitCode.ShouldBe(0, output);
    }

    private sealed class RecordingExtensionBuildRunner(Action? onRun = null) : IExtensionProjectBuildRunner
    {
        public List<ExtensionProjectBuildInvocation> Invocations { get; } = [];

        public Task<int> RunAsync(ExtensionProjectBuildInvocation invocation, CancellationToken cancellationToken)
        {
            Invocations.Add(invocation);
            onRun?.Invoke();
            return Task.FromResult(0);
        }
    }

    private sealed class ExtensionBuildFixture : IDisposable
    {
        private ExtensionBuildFixture(string root, string projectPath, string stagingDirectory, string repoRoot)
            => (Root, ProjectPath, StagingDirectory, BotNexusRepoRoot) = (root, projectPath, stagingDirectory, repoRoot);

        public string Root { get; }
        public string ProjectPath { get; }
        public string StagingDirectory { get; }
        public string BotNexusRepoRoot { get; }

        public static ExtensionBuildFixture Create(string fixtureName)
        {
            var root = Path.Combine(Path.GetTempPath(), $"bn-3902-{Guid.NewGuid():N}");
            var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "OutOfTreeExtensionBuild", fixtureName);
            var repository = Path.Combine(root, "external repository");
            Directory.CreateDirectory(repository);
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(source, file);
                if (relativePath.EndsWith(".csproj.template", StringComparison.Ordinal))
                    relativePath = relativePath[..^".template".Length];
                var destination = Path.Combine(repository, relativePath);
                var destinationDirectory = Path.GetDirectoryName(destination)
                    ?? throw new InvalidOperationException($"Fixture destination has no parent: {destination}");
                Directory.CreateDirectory(destinationDirectory);
                File.Copy(file, destination);
            }

            return new ExtensionBuildFixture(root, Path.Combine(repository, $"Contract.{fixtureName}.csproj"),
                Path.Combine(root, "staging output"), FindRepoRoot());
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
                directory = directory.Parent;
            return directory?.FullName
                ?? throw new InvalidOperationException("Could not locate the BotNexus repository root.");
        }
    }
}

using BotNexus.Cli.Commands;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Pins the observable build-scoping decisions from #2493: which MSBuild file is handed to
/// <c>dotnet build</c>, and exactly which properties are passed on each attempt.
///
/// These assert the DECISION, not the duration. The speed win comes entirely from pointing
/// MSBuild at the deployment traversal project instead of the full solution, and from no longer
/// perturbing every project's generated assembly info with a fresh <c>SourceRevisionId</c>. Both
/// are directly observable in the resolved path and the argument string.
/// </summary>
public sealed class BuildTargetResolutionTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bn-2493-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ResolveBuildTarget_PrefersDeploymentProject_WhenBothExist()
    {
        var root = NewTempDir();
        try
        {
            var deploy = Path.Combine(root, BuildCommand.DeployProjectFileName);
            File.WriteAllText(deploy, "<Project />");
            File.WriteAllText(Path.Combine(root, BuildCommand.SolutionFileName), "<Solution />");

            BuildCommand.ResolveBuildTarget(root).ShouldBe(deploy);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveBuildTarget_FallsBackToSolution_WhenDeploymentProjectIsMissing()
    {
        // Safe direction: an older deployment repo without the traversal project must still build
        // EVERYTHING rather than build nothing.
        var root = NewTempDir();
        try
        {
            var solution = Path.Combine(root, BuildCommand.SolutionFileName);
            File.WriteAllText(solution, "<Solution />");

            BuildCommand.ResolveBuildTarget(root).ShouldBe(solution);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveBuildTarget_ReturnsNull_WhenNeitherFileExists()
    {
        var root = NewTempDir();
        try
        {
            BuildCommand.ResolveBuildTarget(root).ShouldBeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeployProject_NameAndSolutionName_AreTheExpectedFiles()
    {
        BuildCommand.DeployProjectFileName.ShouldBe("BotNexus.Deploy.proj");
        BuildCommand.SolutionFileName.ShouldBe("BotNexus.slnx");
    }

    [Fact]
    public void BuildArguments_PassShaAsScopedProperty_NotSolutionWideSourceRevisionId()
    {
        // The whole point of #2493's SourceRevisionId scoping: passing /p:SourceRevisionId
        // solution-wide changes generated AssemblyInfo for every project on every commit and
        // defeats MSBuild's up-to-date check. It must NOT appear in the argument string.
        var args = BuildOutputStreamer.BuildArguments("X.proj", "abc123", isolatedCompilation: false);

        args.ShouldContain("/p:BotNexusSourceRevisionId=abc123");
        args.ShouldNotContain("/p:SourceRevisionId=");
    }

    [Fact]
    public void BuildArguments_DifferentShas_ProduceDifferentArguments()
    {
        // Scoping the SHA must not collapse two materially different builds onto one key: the
        // value still reaches MSBuild, it is just consumed by fewer projects.
        var a = BuildOutputStreamer.BuildArguments("X.proj", "aaaaaaa", isolatedCompilation: false);
        var b = BuildOutputStreamer.BuildArguments("X.proj", "bbbbbbb", isolatedCompilation: false);

        a.ShouldNotBe(b);
        a.ShouldContain("aaaaaaa");
        b.ShouldContain("bbbbbbb");
    }

    [Fact]
    public void BuildArguments_FirstAttempt_LeavesCompilerServerAndNodeReuseEnabled()
    {
        var args = BuildOutputStreamer.BuildArguments("X.proj", "abc123", isolatedCompilation: false);

        args.ShouldNotContain("/nodeReuse:false");
        args.ShouldNotContain("/p:UseSharedCompilation=false");
    }

    [Fact]
    public void BuildArguments_IsolatedRetry_DisablesCompilerServerAndNodeReuse()
    {
        var args = BuildOutputStreamer.BuildArguments("X.proj", "abc123", isolatedCompilation: true);

        args.ShouldContain("/nodeReuse:false");
        args.ShouldContain("/p:UseSharedCompilation=false");
    }

    [Fact]
    public void BuildArguments_AlwaysSkipTestsAndCli_AndBuildRelease()
    {
        foreach (var isolated in new[] { false, true })
        {
            var args = BuildOutputStreamer.BuildArguments("X.proj", "abc123", isolated);

            args.ShouldContain("/p:SkipTests=true");
            args.ShouldContain("/p:SkipCli=true");
            args.ShouldContain("-c Release");
            args.ShouldStartWith("build \"X.proj\"");
        }
    }

    /// <summary>
    /// The deployment closure is the risky part of #2493: anything under <c>src/</c> that the
    /// running gateway needs but the traversal project does not build becomes a stale binary.
    /// PR #2398 fixed exactly this shape of defect for extensions. Assert against the real repo
    /// that the closure is a wildcard over <c>src/</c> and that it therefore includes every
    /// extension project and the Blazor portal client.
    /// </summary>
    [Fact]
    public void DeployProject_CoversEveryProjectUnderSrc_IncludingExtensionsAndBlazorPortal()
    {
        var repoRoot = FindRepoRoot();
        var deployProject = Path.Combine(repoRoot, BuildCommand.DeployProjectFileName);
        File.Exists(deployProject).ShouldBeTrue($"{deployProject} must exist at the repo root");

        var text = File.ReadAllText(deployProject);
        text.ShouldContain(@"src\**\*.csproj");

        var srcProjects = Directory
            .GetFiles(Path.Combine(repoRoot, "src"), "*.csproj", SearchOption.AllDirectories);

        srcProjects.Length.ShouldBeGreaterThan(0);
        srcProjects.ShouldContain(p => p.EndsWith("BotNexus.Gateway.Api.csproj", StringComparison.Ordinal));
        srcProjects.ShouldContain(p => p.EndsWith(
            "BotNexus.Extensions.Channels.SignalR.BlazorClient.csproj", StringComparison.Ordinal));
        srcProjects.Count(p => p.Contains($"{Path.DirectorySeparatorChar}extensions{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal)).ShouldBeGreaterThan(1);
    }

    /// <summary>
    /// The traversal project narrows the set to <c>src/</c>. Prove that nothing the deployment
    /// needs lives outside it: every project the solution lists is either under <c>src/</c>,
    /// under <c>tests/</c> (never deployed), or under <c>examples/</c> (never deployed).
    /// </summary>
    [Fact]
    public void SolutionProjectsOutsideSrc_AreOnlyTestsAndExamples()
    {
        var repoRoot = FindRepoRoot();
        var slnx = File.ReadAllText(Path.Combine(repoRoot, BuildCommand.SolutionFileName));

        var paths = System.Text.RegularExpressions.Regex
            .Matches(slnx, "Path=\"([^\"]+\\.csproj)\"")
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .ToList();

        paths.Count.ShouldBeGreaterThan(50);

        var outside = paths
            .Where(p => !p.StartsWith("src/", StringComparison.Ordinal))
            .Where(p => !p.StartsWith("tests/", StringComparison.Ordinal))
            .Where(p => !p.StartsWith("examples/", StringComparison.Ordinal))
            .ToList();

        outside.ShouldBeEmpty(
            "a deployed project outside src/ would never be built by BotNexus.Deploy.proj: "
            + string.Join(", ", outside));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, BuildCommand.SolutionFileName)))
            dir = dir.Parent;

        dir.ShouldNotBeNull("could not locate the repository root from the test output directory");
        return dir!.FullName;
    }

    [Fact]
    public void LockedFilesExitCode_IsDistinctFromNormalBuildFailure()
    {
        BuildOutputStreamer.LockedFilesExitCode.ShouldNotBe(0);
        BuildOutputStreamer.LockedFilesExitCode.ShouldNotBe(1);
        BuildOutputStreamer.LockedFilesExitCode.ShouldBe(75);
    }
}

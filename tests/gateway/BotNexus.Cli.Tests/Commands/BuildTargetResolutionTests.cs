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
    public void ResolveBuildTarget_ReturnsDeploymentProject_WhenItExists()
    {
        var root = NewTempDir();
        try
        {
            var deploy = Path.Combine(root, BuildCommand.DeployProjectFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(deploy)!);
            File.WriteAllText(deploy, "<Project />");

            BuildCommand.ResolveBuildTarget(root).ShouldBe(deploy);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveBuildTarget_ReturnsNull_WhenDeploymentProjectIsMissing()
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
    public void DeployProject_NameIsTheExpectedFile()
    {
        BuildCommand.DeployProjectFileName.ShouldBe("src/dirs.proj");
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
        text.ShouldContain(@"**\*.csproj");

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
    /// needs lives outside it: every project on disk is either under <c>src/</c>,
    /// under <c>tests/</c> (never deployed), under <c>examples/</c> (never deployed), or under
    /// <c>tools/</c> (compile-time-only analyzers and source generators, referenced as Analyzer
    /// rather than as an assembly, so nothing they produce is shipped either).
    /// </summary>
    [Fact]
    public void ProjectsOutsideSrc_AreOnlyTestsExamplesAndTools()
    {
        var repoRoot = FindRepoRoot();
        var paths = Directory.GetFiles(repoRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetRelativePath(repoRoot, p).Replace('\\', '/'))
            .ToList();

        paths.Count.ShouldBeGreaterThan(50);

        var outside = paths
            .Where(p => !p.StartsWith("src/", StringComparison.Ordinal))
            .Where(p => !p.StartsWith("tests/", StringComparison.Ordinal))
            .Where(p => !p.StartsWith("examples/", StringComparison.Ordinal))
            // tools/ is build-time tooling: the feature-flag source generator (#2769) targets
            // netstandard2.0, loads into the Roslyn compiler host, and is consumed with
            // ReferenceOutputAssembly="false". It emits source into its consumer rather than an
            // artefact of its own, so its absence from src/dirs.proj is correct, not a gap.
            // ToolsProjects: ToolsProjects_AreCompileTimeOnly_AndShipNothing keeps
            // this exclusion narrow.
            .Where(p => !p.StartsWith("tools/", StringComparison.Ordinal))
            .ToList();

        outside.ShouldBeEmpty(
            "a deployed project outside src/ would never be built by src/dirs.proj: "
            + string.Join(", ", outside));
    }

    /// <summary>
    /// The <c>tools/</c> exclusion above must stay narrow. A project there that is
    /// NOT a compile-time-only analyzer would be silently dropped from the deployment closure - the
    /// exact failure the fence exists to catch - so each one has to prove it ships nothing.
    /// <para>
    /// Scoped to source generators deliberately. <c>tools/</c> also holds standalone utilities
    /// such as <c>BotNexus.Probe</c> that are deployed independently.
    /// </para>
    /// </summary>
    [Fact]
    public void SourceGeneratorProjects_AreCompileTimeOnly_AndShipNothing()
    {
        var repoRoot = FindRepoRoot();
        var toolsProjects = Directory.GetFiles(Path.Combine(repoRoot, "tools"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => Path.GetFileNameWithoutExtension(p).Contains("SourceGenerator", StringComparison.Ordinal))
            .Select(p => Path.GetRelativePath(repoRoot, p).Replace('\\', '/'))
            .ToList();

        var violations = new List<string>();

        foreach (var relative in toolsProjects)
        {
            var absolute = Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(absolute).ShouldBeTrue($"{relative} does not exist");

            var text = File.ReadAllText(absolute);

            if (!text.Contains("<TargetFramework>netstandard2.0</TargetFramework>", StringComparison.Ordinal))
                violations.Add($"{relative}: must target netstandard2.0 to load in the Roslyn analyzer host.");

            if (!text.Contains("<IncludeBuildOutput>false</IncludeBuildOutput>", StringComparison.Ordinal))
                violations.Add($"{relative}: must set IncludeBuildOutput=false - a tools/ project ships nothing.");
        }

        violations.ShouldBeEmpty(
            "source-generator projects are excluded from the deployment closure, so each must "
            + "be compile-time-only:\n" + string.Join("\n", violations));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
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

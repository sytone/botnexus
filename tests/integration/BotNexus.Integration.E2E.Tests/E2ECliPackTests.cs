using System.Text.RegularExpressions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Pins the pack contract of <see cref="NewUserExperienceFixture"/> (issue #3388).
///
/// These are pure unit tests over <see cref="E2ECliPack"/> and the fixture source text: they run
/// in milliseconds and need neither a pack nor a gateway, so the contract that the #3388 failure
/// violated is checkable without spending a 20-minute gate to observe the symptom.
///
/// The defect they exclude: passing the synthetic <c>99.99.99-e2e-*</c> stamp as MSBuild
/// <c>Version</c> (not just <c>PackageVersion</c>) propagates it through every
/// <c>ProjectReference</c>, so the packed CLI binds against assembly version <c>99.99.99.0</c>
/// while the dependency assemblies sitting in the shared Release tree carry the repo version.
/// The installed tool then dies inside <c>init</c> with
/// <c>Could not load file or assembly 'BotNexus.Agent.Providers.Core, Version=99.99.99.0'</c>.
/// </summary>
public sealed class E2ECliPackTests
{
    private const string RunId = "deadbeefcafef00d";

    /// <summary>
    /// The load-bearing assertion. Reverting the fixture to <c>/p:Version=99.99.99-...</c> reddens
    /// exactly this test, and it does so without needing the failure to reproduce in a container.
    /// </summary>
    [Fact]
    public void PackDoesNotStampTheSyntheticVersionOntoAssemblies()
    {
        var args = E2ECliPack.BuildPackArguments(
            "/repo/src/gateway/BotNexus.Cli/BotNexus.Cli.csproj",
            new Version(0, 44, 0),
            E2ECliPack.BuildPackageVersion(RunId),
            "/sandbox/pack",
            "/sandbox/build");

        var version = Regex.Match(args, @"/p:Version=(?<v>\S+)");
        version.Success.ShouldBeTrue("the pack must state an explicit assembly version");
        version.Groups["v"].Value.ShouldBe(
            "0.44.0",
            "MSBuild Version flows into every ProjectReference; stamping it with the synthetic " +
            "pack version rebuilds the whole closure at 99.99.99.0 and the installed CLI then " +
            "fails to bind against the repo-versioned dependencies in bin/Release (issue #3388)");
        version.Groups["v"].Value.ShouldNotStartWith("99.99.99");
    }

    /// <summary>
    /// The package still needs a per-run identity, because <c>dotnet tool install --version</c>
    /// selects by it and a shared feed must not serve a previous run's payload.
    /// </summary>
    [Fact]
    public void PackStillGivesThePackageAUniquePerRunVersion()
    {
        var packageVersion = E2ECliPack.BuildPackageVersion(RunId);
        packageVersion.ShouldBe("99.99.99-e2e-deadbeef");

        var args = E2ECliPack.BuildPackArguments(
            "/repo/cli.csproj", new Version(0, 44, 0), packageVersion, "/sandbox/pack", "/sandbox/build");
        Assert.Contains($"/p:PackageVersion={packageVersion}", args);

        E2ECliPack.BuildPackageVersion("0000000011111111")
            .ShouldNotBe(packageVersion, "each run must get its own package identity");
    }

    /// <summary>
    /// The bound version is a function of the assembly version, never of the package stamp. This is
    /// the invariant the install-layout guard in the fixture checks against.
    /// </summary>
    [Theory]
    [InlineData(0, 44, 0)]
    [InlineData(1, 2, 3)]
    public void BoundVersionFollowsTheAssemblyVersionNotThePackageStamp(int major, int minor, int build)
    {
        E2ECliPack.ExpectedBoundVersion(new Version(major, minor, build))
            .ShouldBe(new Version(major, minor, build, 0));
    }

    /// <summary>
    /// Node reuse and shared compilation must stay disabled: a retained build node holds our
    /// captured stdout open and the pack appears to hang until the fixture's timeout fires.
    /// </summary>
    [Fact]
    public void PackDisablesBuildServersSoTheProcessReturns()
    {
        var args = E2ECliPack.BuildPackArguments(
            "/repo/cli.csproj", new Version(0, 44, 0), "99.99.99-e2e-deadbeef", "/sandbox/pack", "/sandbox/build");
        Assert.Contains("/nodeReuse:false", args);
        Assert.Contains("/p:UseSharedCompilation=false", args);
        Assert.Contains("--configuration Release", args);
    }

    /// <summary>
    /// The pack must redirect its intermediate/output artifacts into the per-run sandbox.
    ///
    /// <para>This clause arrived on <c>main</c> while this branch was open and is the OTHER half of
    /// the same defect. <c>PackageVersion</c>-only stamping stops the pack from asking for a
    /// version the Release tree does not contain; <c>ArtifactsPath</c> stops the pack from WRITING
    /// that shared tree at all. Both are needed: without the stamp fix the pack requests a
    /// nonexistent version, and without the isolation a concurrent Release build can still tear the
    /// output from underneath it (#3255).</para>
    /// </summary>
    [Fact]
    public void PackIsolatesItsArtifactsFromTheSharedReleaseTree()
    {
        var args = E2ECliPack.BuildPackArguments(
            "/repo/cli.csproj", new Version(0, 44, 0), "99.99.99-e2e-deadbeef", "/sandbox/pack", "/sandbox/build");

        Assert.Contains("/p:ArtifactsPath=\"/sandbox/build\"", args);
    }

    /// <summary>
    /// The repo version is read from this assembly rather than parsed out of a props file, so the
    /// pack cannot ask for a version the runner's Release build did not produce. Assert it is a
    /// real version and not the synthetic stamp, which would mean the whole scheme had inverted.
    /// </summary>
    [Fact]
    public void RepoAssemblyVersionIsTheRealBuildVersion()
    {
        var v = E2ECliPack.RepoAssemblyVersion;
        v.ShouldNotBe(new Version(0, 0, 0, 0), "this test assembly must carry a stamped version");
        v.Major.ShouldNotBe(99, "the repo version must never be the synthetic pack stamp");
    }

    /// <summary>
    /// The fixture must route its pack through <see cref="E2ECliPack.BuildPackArguments"/> rather
    /// than assembling a second command line that could drift. Source-text check, because the
    /// alternative is to run a real pack.
    /// </summary>
    [Fact]
    public void FixtureBuildsItsPackCommandThroughTheSharedHelper()
    {
        var source = ReadFixtureSource();

        // Asserted via Assert rather than Shouldly: this project carries a local ShouldlyShim
        // whose string overloads shadow Shouldly's richer ones.
        Assert.Contains("E2ECliPack.BuildPackArguments", source);
        Regex.IsMatch(source, @"/p:Version=\{?\$?\w*[Pp]ackVersion").ShouldBeFalse(
            "the synthetic pack version must never be passed as MSBuild Version (issue #3388)");
    }

    /// <summary>
    /// The pack writes the shared Release tree, exactly as the solution prebuild does, so it must
    /// hold the same machine-wide mutex (#2739 extended by #3388). Two E2E hosts packing at once
    /// is the torn-read that produced the original binding failure.
    /// </summary>
    [Fact]
    public void FixtureSerialisesThePackBehindThePrebuildMutex()
    {
        var source = ReadFixtureSource();
        Assert.Contains("E2ECliPack.PrebuildMutexName", source);
        // Must be visible across sessions, not only the current one.
        Assert.StartsWith(@"Global\", E2ECliPack.PrebuildMutexName, StringComparison.Ordinal);
    }

    private static string ReadFixtureSource()
    {
        var repoRoot = RepoLocator.FindRepoRoot();
        var path = Path.Combine(
            repoRoot, "tests", "integration", "BotNexus.Integration.E2E.Tests",
            "NewUserExperienceFixture.cs");
        File.Exists(path).ShouldBeTrue($"fixture source not found at '{path}'");
        return File.ReadAllText(path);
    }
}

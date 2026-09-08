using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace BotNexus.Integration.Cli.Tests;

/// <summary>
/// Non-vacuity coverage for the #3255 pack-isolation guard.
///
/// The defect this guards against is intermittent: an un-isolated pack races the concurrent
/// Release build of <c>src/dirs.proj</c> and only sometimes produces a torn package. A guard
/// exercised solely against the healthy real pack would therefore pass forever without ever
/// demonstrating that it can fail. These cases drive the guard directly with a command line that
/// has had the redirect removed, and with an artifacts directory that stayed empty, and assert it
/// reports each case by name.
///
/// No process is invoked and no package is produced here, so these cases are deterministic and
/// cost nothing.
/// </summary>
public sealed class CliPackIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "botnexus-pack-isolation-tests", Guid.NewGuid().ToString("N"));

    public CliPackIsolationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static readonly Version RepoVersion = new(0, 45, 0);

    private string Args(string artifactsDir) => CliPackIsolation.BuildPackArguments(
        cliProject: "/repo/src/gateway/BotNexus.Cli/BotNexus.Cli.csproj",
        assemblyVersion: RepoVersion,
        packVersion: "99.99.99-local-deadbeef",
        packOutputDir: Path.Combine(_root, "pack"),
        artifactsDir: artifactsDir);

    [Fact]
    public void BuildPackArguments_CarriesEveryIsolationSwitch()
    {
        var args = Args(Path.Combine(_root, "build"));

        CliPackIsolation.FindMissingIsolationSwitches(args).ShouldBeEmpty(
            $"The constructed pack command line must request full isolation.\nArgs: {args}");
    }

    [Fact]
    public void BuildPackArguments_RedirectsArtifactsIntoTheSandbox_NotTheRepo()
    {
        var artifacts = Path.Combine(_root, "build");
        var args = Args(artifacts);

        args.ShouldContain($"/p:ArtifactsPath=\"{artifacts}\"");
        args.ShouldContain("--configuration Release");
        args.ShouldContain("/p:PackageVersion=99.99.99-local-deadbeef");
    }

    /// <summary>
    /// The guard must FIRE when the redirect is absent. Removing the switch is the exact
    /// regression #3255 leaves the codebase exposed to, so it is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void FindMissingIsolationSwitches_NamesTheRedirect_WhenItIsRemoved()
    {
        var artifacts = Path.Combine(_root, "build");
        var unIsolated = Args(artifacts).Replace($"/p:ArtifactsPath=\"{artifacts}\" ", string.Empty);

        var missing = CliPackIsolation.FindMissingIsolationSwitches(unIsolated);

        missing.ShouldContain("/p:ArtifactsPath=");
        missing.Count.ShouldBe(1, $"Only the redirect was removed.\nArgs: {unIsolated}");
    }

    [Fact]
    public void FindMissingIsolationSwitches_NamesNodeReuseAndSharedCompilation_WhenRemoved()
    {
        var unIsolated = Args(Path.Combine(_root, "build"))
            .Replace("/nodeReuse:false ", string.Empty)
            .Replace("/p:UseSharedCompilation=false ", string.Empty);

        var missing = CliPackIsolation.FindMissingIsolationSwitches(unIsolated);

        missing.ShouldContain("/nodeReuse:false");
        missing.ShouldContain("/p:UseSharedCompilation=false");
    }

    [Fact]
    public void ArtifactsDirWasPopulated_IsFalse_ForAnEmptyOrAbsentDirectory()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        CliPackIsolation.ArtifactsDirWasPopulated(empty).ShouldBeFalse(
            "An empty artifacts directory means the pack built somewhere else.");
        CliPackIsolation.ArtifactsDirWasPopulated(Path.Combine(_root, "never-created")).ShouldBeFalse();
        CliPackIsolation.ArtifactsDirWasPopulated(string.Empty).ShouldBeFalse();
    }

    [Fact]
    public void ArtifactsDirWasPopulated_IsTrue_WhenTheRedirectReceivedOutput()
    {
        var populated = Path.Combine(_root, "populated", "bin", "BotNexus.Cli", "release");
        Directory.CreateDirectory(populated);
        File.WriteAllText(Path.Combine(populated, "BotNexus.Cli.dll"), "not a real assembly");

        CliPackIsolation.ArtifactsDirWasPopulated(Path.Combine(_root, "populated")).ShouldBeTrue();
    }

    [Fact]
    public void DescribeIsolationFailure_NamesTheMissingSwitch()
    {
        var artifacts = Path.Combine(_root, "build");
        var unIsolated = Args(artifacts).Replace($"/p:ArtifactsPath=\"{artifacts}\" ", string.Empty);

        var message = CliPackIsolation.DescribeIsolationFailure(unIsolated, artifacts);

        message.ShouldContain("/p:ArtifactsPath=");
        message.ShouldContain("3255");
        message.ShouldContain(unIsolated);
    }

    /// <summary>
    /// The subtler failure: the switch is present but MSBuild wrote elsewhere anyway. The message
    /// must say that rather than repeating "missing switch", or a reader will chase the wrong
    /// cause.
    /// </summary>
    [Fact]
    public void DescribeIsolationFailure_ReportsAnUnpopulatedRedirect_Distinctly()
    {
        var artifacts = Path.Combine(_root, "build");
        var isolated = Args(artifacts);

        var message = CliPackIsolation.DescribeIsolationFailure(isolated, artifacts);

        message.ShouldContain("received no build output");
        message.ShouldNotContain("missing required isolation switch");
        message.ShouldContain("shared bin/obj trees");
    }

    // ---- #3237: the synthetic stamp must identify the PACKAGE only ----------------------------

    /// <summary>
    /// The load-bearing assertion for #3237. Reverting this to <c>/p:Version=99.99.99-...</c>
    /// reddens this case, which is the whole point: <c>Version</c> is a global MSBuild property,
    /// so a synthetic value there propagates through every <c>ProjectReference</c> and makes the
    /// CLI bind a dependency version that no Release build on the machine ever produced.
    /// </summary>
    [Fact]
    public void BuildPackArguments_StampsTheSyntheticVersionOnThePackageOnly()
    {
        var args = Args(Path.Combine(_root, "build"));

        var version = Regex.Match(args, @"/p:Version=(?<v>\S+)");
        version.Success.ShouldBeTrue($"Pack must pass an explicit /p:Version.\nArgs: {args}");
        version.Groups["v"].Value.ShouldBe(RepoVersion.ToString(3),
            "MSBuild Version must stay at the repo assembly version; only PackageVersion is synthetic (#3237).");

        args.ShouldContain("/p:PackageVersion=99.99.99-local-deadbeef");
        args.ShouldNotContain("/p:Version=99.99.99",
            customMessage: "Stamping the synthetic version as MSBuild Version is the #3237 defect.");
    }

    /// <summary>
    /// Source-level guard against the defect being reintroduced by editing the builder rather than
    /// its call site. A string interpolation putting the pack stamp into <c>/p:Version=</c> is the
    /// exact shape that produced the intermittent load failure.
    /// </summary>
    [Fact]
    public void PackArgumentBuilderSource_DoesNotInterpolateThePackStampIntoMsBuildVersion()
    {
        var source = ReadSource("CliPackIsolation.cs");

        Regex.IsMatch(source, @"/p:Version=\{?\$?\w*[Pp]ackVersion").ShouldBeFalse(
            "CliPackIsolation must not interpolate the pack stamp into MSBuild Version (#3237).");
        source.ShouldContain("/p:Version={assemblyVersion.ToString(3)}");
    }

    /// <summary>
    /// The guard's expected version and the pack's actual <c>Version</c> must be the same number.
    /// If they drift, the layout guard either never fires or fires on every healthy run - both of
    /// which destroy the AC1/AC2 signal this issue exists to protect.
    /// </summary>
    [Fact]
    public void ExpectedBoundVersion_MatchesTheVersionThePackStamps()
    {
        var expected = CliPackIsolation.ExpectedBoundVersion(RepoVersion);

        expected.ShouldBe(new Version(0, 45, 0, 0));
        expected.ToString(3).ShouldBe(
            Regex.Match(Args(Path.Combine(_root, "build")), @"/p:Version=(?<v>\S+)").Groups["v"].Value,
            "The version the guard expects must be the version the pack stamps (#3237).");
        expected.ShouldNotBe(CliInstallLayout.ToAssemblyVersion("99.99.99-local-deadbeef"),
            "Expecting the synthetic stamp is precisely the mistake that left the guard silent.");
    }

    /// <summary>
    /// The live fixture must pass the repo version, not the pack stamp. Asserted against the
    /// fixture's own source so it holds without paying for a real pack.
    /// </summary>
    [Fact]
    public void FixtureSource_PassesTheRepoAssemblyVersionToThePack()
    {
        var source = ReadSource("LocalCliInstallFixture.cs");

        source.ShouldContain("CliPackIsolation.RepoAssemblyVersion");
        source.ShouldContain("CliPackIsolation.ExpectedBoundVersion(AssemblyVersion)");
        source.ShouldNotContain("CliInstallLayout.ToAssemblyVersion(PackVersion)",
            customMessage: "The layout guard must expect the repo version, not the synthetic pack stamp (#3237).");
    }

    private static string ReadSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, fileName)))
            dir = dir.Parent;

        dir.ShouldNotBeNull($"Could not locate {fileName} above {AppContext.BaseDirectory}.");
        return File.ReadAllText(Path.Combine(dir!.FullName, fileName));
    }
}

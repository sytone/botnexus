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

    private string Args(string artifactsDir) => CliPackIsolation.BuildPackArguments(
        cliProject: "/repo/src/gateway/BotNexus.Cli/BotNexus.Cli.csproj",
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
}

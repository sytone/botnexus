using System.IO.Abstractions.TestingHelpers;
using BotNexus.Agent.Core.Tools;

namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>
/// Pins <c>exec</c>'s end of the <c>.ps1</c> shim launch (issue #3710).
/// </summary>
/// <remarks>
/// <para>
/// <c>exec ["qmd","--version"]</c> failed with the bare <c>The system cannot find the path
/// specified.</c> because <c>qmd</c>'s only PATH entry is <c>qmd.ps1</c> - a script, not an
/// executable image - and exec carried its OWN copy of the PATH probe that only knew
/// <c>.exe/.cmd/.bat</c>. #3568 had fixed the sibling <c>.cmd</c> defect in that copy, which is
/// precisely why the two could disagree.
/// </para>
/// <para>
/// These assertions are on the constructed descriptor, so they hold on the Linux CI runner where no
/// <c>.ps1</c> shim exists; a live-process test there would pass vacuously.
/// </para>
/// </remarks>
public class ExecToolPowerShellShimLaunchTests
{
    [Fact]
    public void ResolveCommand_RoutesPs1ShimThroughPowerShellHost()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var launch = ExecTool.ResolveCommand(
            [@"Q:\.tools\.npm-global\qmd.ps1", "--version"],
            new MockFileSystem());

        Path.GetFileNameWithoutExtension(launch.FileName).ToLowerInvariant()
            .ShouldBeOneOf("pwsh", "powershell");
        launch.RawArgumentLine.ShouldBeNull();
        launch.Args.ShouldBe(new[]
        {
            "-NoProfile", "-File", @"Q:\.tools\.npm-global\qmd.ps1", "--version",
        });
    }

    [Fact]
    public void ResolveCommand_PassesSpaceAndDollarArgumentsToPs1Unmodified()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Acceptance criterion 2. A cmd.exe raw line would have quoted the spaced argument and
        // exposed the '$' to another round of parsing; ArgumentList does neither.
        var launch = ExecTool.ResolveCommand(
            [@"C:\shims\qmd.ps1", "search", "Exhaust Sentinel enablement", "$literal"],
            new MockFileSystem());

        launch.Args.ShouldBe(new[]
        {
            "-NoProfile", "-File", @"C:\shims\qmd.ps1",
            "search", "Exhaust Sentinel enablement", "$literal",
        });
        launch.RawArgumentLine.ShouldBeNull();
    }

    [Fact]
    public void ResolveCommand_ResolvesBarePs1CommandFromPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = @"Q:\.tools\.npm-global";
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(Path.Combine(dir, "qmd.ps1"), new MockFileData("# shim"));

        var previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", dir);

            var launch = ExecTool.ResolveCommand(["qmd", "--version"], fileSystem);

            launch.ResolvedTarget.ShouldBe(Path.Combine(dir, "qmd.ps1"));
            launch.Args.ShouldContain(Path.Combine(dir, "qmd.ps1"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previous);
        }
    }

    [Fact]
    public void LaunchFailureDetail_NamesTheResolvedPathRatherThanTheBareOsString()
    {
        // Acceptance criterion 4: the diagnostic must name what was actually attempted. The OS
        // message alone ("The system cannot find the path specified.") named neither the command
        // nor a path, so an agent could not tell the tool, the workingDir and an argument apart.
        var launch = new ProcessLaunch(
            "pwsh.exe",
            ["-NoProfile", "-File", @"Q:\.tools\.npm-global\qmd.ps1", "--version"],
            null,
            @"Q:\.tools\.npm-global\qmd.ps1");

        var detail = launch.FormatLaunchFailureDetail("qmd");

        detail.ShouldContain("qmd");
        detail.ShouldContain(@"Q:\.tools\.npm-global\qmd.ps1");
        detail.ShouldContain("pwsh.exe");
    }
}

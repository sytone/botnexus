using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>
/// Pins the Windows .cmd/.bat shim launch descriptor (issue #3568).
/// </summary>
/// <remarks>
/// The defect was that the cmd.exe payload was pushed through
/// <c>ProcessStartInfo.ArgumentList</c>, which applies CRT quoting and turns the payload's quotes
/// into <c>\"</c>. cmd.exe does not understand backslash-escaped quotes, so it carried them into the
/// program name and failed with <c>'"C:\Program Files\nodejs\npm.cmd"' is not recognized</c>.
/// These assertions are on the CONSTRUCTED descriptor, not on a live process, so they run and hold
/// on the Linux CI runner where no npm.cmd shim exists at all - a live-process test there would pass
/// vacuously.
/// </remarks>
public class ExecToolCmdShimLaunchTests
{
    [Fact]
    public void RawArgumentLine_QuotesShimPathContainingSpaces()
    {
        var line = ExecTool.BuildCmdRawArgumentLine(@"C:\Program Files\nodejs\npm.cmd", ["--version"]);

        // Outer frame present, inner path quoted, and NO backslash-escaped quotes anywhere: a
        // single \" in this line is the exact byte sequence that produced the reported failure.
        line.ShouldStartWith("/d /s /c \"");
        line.ShouldEndWith("\"");
        line.ShouldContain("\"C:\\Program Files\\nodejs\\npm.cmd\"");
        line.ShouldContain("--version");
        line.ShouldNotContain("\\\"");
    }

    [Fact]
    public void RawArgumentLine_LeavesUnspacedShimPathUnquoted()
    {
        var line = ExecTool.BuildCmdRawArgumentLine(@"C:\tools\az.cmd", ["account", "show"]);

        line.ShouldBe("/d /s /c \"C:\\tools\\az.cmd account show\"");
    }

    [Fact]
    public void RawArgumentLine_QuotesArgumentsContainingSpaces()
    {
        var line = ExecTool.BuildCmdRawArgumentLine(@"C:\tools\npx.cmd", ["run", "my task"]);

        line.ShouldBe("/d /s /c \"C:\\tools\\npx.cmd run \"my task\"\"");
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("az")]
    [InlineData("npx")]
    [InlineData("yarn")]
    public void ResolveCommand_RoutesShimThroughCmdWithRawLine(string shim)
    {
        // Force the Windows shim branch deterministically on any OS by resolving an explicit
        // .cmd path - Path.HasExtension short-circuits the PATH probe, so this is not runner
        // dependent beyond the OS check inside ResolveCommand itself.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var launch = ExecTool.ResolveCommand(
            [$@"C:\shims\{shim}.cmd", "--version"],
            new MockFileSystem());

        launch.FileName.ShouldNotStartWith("\"");
        Path.GetFileName(launch.FileName).ToLowerInvariant().ShouldBe("cmd.exe");
        launch.Args.ShouldBeEmpty();
        launch.RawArgumentLine.ShouldBe($"/d /s /c \"C:\\shims\\{shim}.cmd --version\"");
    }

    [Fact]
    public void ResolveCommand_LeavesNonShimExecutableAsStructuredArguments()
    {
        // Regression guard: git/dotnet/an absolute .exe must NOT acquire a raw line or a cmd.exe
        // wrapper - they are launched directly and their arguments stay individually escaped.
        var launch = ExecTool.ResolveCommand(
            [OperatingSystem.IsWindows() ? @"C:\Program Files\Git\bin\git.exe" : "/usr/bin/git", "status"],
            new MockFileSystem());

        launch.RawArgumentLine.ShouldBeNull();
        launch.Args.ShouldBe(new[] { "status" });
        launch.FileName.ShouldNotContain("\"");
        launch.FileName.ShouldEndWith(OperatingSystem.IsWindows() ? "git.exe" : "git");
    }
}

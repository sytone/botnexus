using System.Diagnostics;
using BotNexus.Agent.Core.Tools;
using BotNexus.Extensions.Mcp.Transport;

namespace BotNexus.Extensions.Mcp.Tests;

/// <summary>
/// Pins the Windows .cmd/.bat shim launch path for the MCP stdio transport (issue #3642).
/// </summary>
/// <remarks>
/// The defect: <see cref="StdioMcpTransport"/> carried its own copy of the exec-tool shim resolver
/// and assembled the cmd.exe payload as a fourth <c>ArgumentList</c> entry. .NET applies CRT quoting
/// to every entry, so the payload's quotes came out as <c>\"</c>; cmd.exe does not recognise
/// backslash-escaped quotes and carried them into the program name, failing with
/// <c>'"C:\Program Files\nodejs\npm.cmd"' is not recognized</c>. Every npx-launched stdio MCP server
/// on Windows failed to start.
/// <para>
/// These assertions are on the CONSTRUCTED command line, not on a live process, so they hold on the
/// Linux CI runner where no npm.cmd exists at all - a live-process test there would pass vacuously.
/// The PATH probe is driven through an injected existence predicate for the same reason.
/// </para>
/// </remarks>
public class StdioTransportCmdShimLaunchTests
{
    [Fact]
    public void ApplyArgumentsTo_PutsRawLineOnArgumentsAndLeavesArgumentListEmpty()
    {
        // The non-vacuity anchor: if the raw payload ever goes back through ArgumentList, .NET
        // re-escapes it and ArgumentList stops being empty. Both halves are asserted.
        var launch = new ProcessLaunch("cmd.exe", [], "/d /s /c \"C:\\tools\\npx.cmd -y server\"");
        var startInfo = new ProcessStartInfo { FileName = launch.FileName };

        launch.ApplyArgumentsTo(startInfo);

        startInfo.ArgumentList.ShouldBeEmpty();
        startInfo.Arguments.ShouldBe("/d /s /c \"C:\\tools\\npx.cmd -y server\"");
        startInfo.Arguments.ShouldNotContain("\\\"");
    }

    [Fact]
    public void ApplyArgumentsTo_UsesArgumentListWhenThereIsNoRawLine()
    {
        // Regression guard on the other branch: a plain executable must keep per-argument escaping.
        var launch = new ProcessLaunch("node", ["server.js", "--port", "1234"]);
        var startInfo = new ProcessStartInfo { FileName = launch.FileName };

        launch.ApplyArgumentsTo(startInfo);

        startInfo.Arguments.ShouldBeNullOrEmpty();
        startInfo.ArgumentList.ShouldBe(new[] { "server.js", "--port", "1234" });
    }

    [Fact]
    public void RawArgumentLine_QuotesShimPathContainingSpaces()
    {
        var line = @"C:\Program Files\nodejs\npx.cmd".BuildCmdRawArgumentLine(
            ["-y", "@modelcontextprotocol/server-filesystem"]);

        line.ShouldStartWith("/d /s /c \"");
        line.ShouldEndWith("\"");
        line.ShouldContain("\"C:\\Program Files\\nodejs\\npx.cmd\"");
        line.ShouldContain("@modelcontextprotocol/server-filesystem");

        // A single \" anywhere in this line is the exact byte sequence that produced the failure.
        line.ShouldNotContain("\\\"");
    }

    [Fact]
    public void RawArgumentLine_LeavesUnspacedShimPathUnquoted()
    {
        @"C:\tools\npx.cmd".BuildCmdRawArgumentLine(["-y", "some-server"])
            .ShouldBe("/d /s /c \"C:\\tools\\npx.cmd -y some-server\"");
    }

    [Theory]
    [InlineData("npx")]
    [InlineData("npm")]
    [InlineData("yarn")]
    public void Resolve_RoutesShimThroughCmdWithRawLine(string shim)
    {
        // Explicit .cmd path short-circuits the PATH probe, so the only runner dependence left is
        // the OS check inside Resolve itself.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var launch = WindowsShimLaunch.Resolve($@"C:\shims\{shim}.cmd", ["-y", "server"]);

        Path.GetFileName(launch.FileName).ToLowerInvariant().ShouldBe("cmd.exe");
        launch.FileName.ShouldNotStartWith("\"");
        launch.Args.ShouldBeEmpty();
        launch.RawArgumentLine.ShouldBe($"/d /s /c \"C:\\shims\\{shim}.cmd -y server\"");
    }

    [Fact]
    public void Resolve_ProbesPathForBareCommandAndFindsTheCmdShim()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Injected probe: pretend PATH's first entry holds npx.cmd but no npx.exe.
        var dir = @"C:\fake-path";
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", dir);
        try
        {
            var launch = WindowsShimLaunch.Resolve(
                "npx",
                ["-y", "server"],
                p => string.Equals(p, Path.Combine(dir, "npx.cmd"), StringComparison.OrdinalIgnoreCase));

            launch.RawArgumentLine.ShouldBe($"/d /s /c \"{Path.Combine(dir, "npx.cmd")} -y server\"");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
        }
    }

    [Fact]
    public void Resolve_LeavesNonShimExecutableAsStructuredArguments()
    {
        var launch = WindowsShimLaunch.Resolve(
            OperatingSystem.IsWindows() ? @"C:\Program Files\nodejs\node.exe" : "/usr/bin/node",
            ["server.js"]);

        launch.RawArgumentLine.ShouldBeNull();
        launch.Args.ShouldBe(new[] { "server.js" });
        launch.FileName.ShouldNotContain("\"");
    }

    [Fact]
    public void BuildStartInfo_DoesNotSmuggleACmdPayloadThroughArgumentList()
    {
        // End-to-end on the transport's own seam: whatever the resolver returns, the two argument
        // channels are mutually exclusive. A payload beginning with "/d /s /c" appearing as an
        // ArgumentList entry is the defect signature and must never occur.
        var transport = new StdioMcpTransport("node", ["server.js"]);

        var startInfo = transport.BuildStartInfo();

        startInfo.ArgumentList.ShouldNotContain(a => a.StartsWith("/d /s /c", StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(startInfo.Arguments))
        {
            startInfo.ArgumentList.ShouldBeEmpty();
        }
    }
}

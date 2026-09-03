using BotNexus.Agent.Core.Tools;

namespace BotNexus.Agent.Core.Tests.Tools;

/// <summary>
/// Pins the Windows <c>.ps1</c> shim launch descriptor (issue #3710).
/// </summary>
/// <remarks>
/// #3568 taught the seam about <c>.cmd</c>/<c>.bat</c> only. A console tool whose sole PATH entry is
/// a PowerShell script - <c>qmd</c>, installed by npm as <c>qmd.ps1</c> - was resolved to a path the
/// OS cannot execute as an image, so <c>exec ["qmd","--version"]</c> died with the bare
/// <c>The system cannot find the path specified.</c>
/// Assertions are on the CONSTRUCTED descriptor rather than a live process, so they hold on the
/// Linux CI runner where no <c>.ps1</c> shim exists at all; a live-process test there would pass
/// vacuously.
/// </remarks>
public class WindowsPowerShellShimLaunchTests
{
    private static Func<string, bool> Exists(params string[] paths)
        => p => paths.Contains(p, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Resolve_RoutesPs1ShimThroughPowerShellHostWithFileSwitch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var launch = WindowsShimLaunch.Resolve(
            @"C:\shims\qmd.ps1",
            ["--version"],
            Exists(@"C:\shims\qmd.ps1"));

        // A .ps1 is not an executable image: it must be handed to a PowerShell host, and it must NOT
        // acquire the cmd.exe raw-line treatment, which would re-parse its arguments under cmd rules.
        Path.GetFileNameWithoutExtension(launch.FileName).ToLowerInvariant()
            .ShouldBeOneOf("pwsh", "powershell");
        launch.RawArgumentLine.ShouldBeNull();
        launch.Args.ShouldBe(new[] { "-NoProfile", "-File", @"C:\shims\qmd.ps1", "--version" });
        launch.ResolvedTarget.ShouldBe(@"C:\shims\qmd.ps1");
    }

    [Fact]
    public void Resolve_PassesPs1ArgumentsThroughUnmodified()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Acceptance criterion 2: an argument containing a space and one containing '$' survive
        // verbatim. Structured ArgumentList is what guarantees this - no shell re-parses them.
        var launch = WindowsShimLaunch.Resolve(
            @"C:\shims\qmd.ps1",
            ["search", "Exhaust Sentinel enablement", "$notAVariable"],
            Exists(@"C:\shims\qmd.ps1"));

        launch.Args.ShouldBe(new[]
        {
            "-NoProfile", "-File", @"C:\shims\qmd.ps1",
            "search", "Exhaust Sentinel enablement", "$notAVariable",
        });
    }

    [Fact]
    public void Resolve_ProbesPathForBareCommandNamingAPs1Shim()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = @"Q:\.tools\.npm-global";
        var previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", dir);

            // The live repro: `qmd` is bare, and its only PATH entry is a .ps1.
            var launch = WindowsShimLaunch.Resolve(
                "qmd",
                ["--version"],
                Exists(Path.Combine(dir, "qmd.ps1")));

            launch.ResolvedTarget.ShouldBe(Path.Combine(dir, "qmd.ps1"));
            launch.Args.ShouldContain("-File");
            launch.Args.ShouldContain(Path.Combine(dir, "qmd.ps1"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previous);
        }
    }

    [Fact]
    public void Resolve_StillRoutesBatchShimsThroughCmd()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Regression guard for #3568: adding .ps1 must not divert .cmd/.bat off the cmd.exe path.
        foreach (var shim in new[] { @"C:\shims\npm.cmd", @"C:\shims\legacy.bat" })
        {
            var launch = WindowsShimLaunch.Resolve(shim, ["--version"], Exists(shim));

            Path.GetFileName(launch.FileName).ToLowerInvariant().ShouldBe("cmd.exe");
            launch.Args.ShouldBeEmpty();
            launch.RawArgumentLine.ShouldBe($"/d /s /c \"{shim} --version\"");
            launch.ResolvedTarget.ShouldBe(shim);
        }
    }

    [Fact]
    public void Resolve_LeavesNativeExecutableStructuredAndUnwrapped()
    {
        var exe = OperatingSystem.IsWindows() ? @"C:\Program Files\Git\bin\git.exe" : "/usr/bin/git";

        var launch = WindowsShimLaunch.Resolve(exe, ["status"], Exists(exe));

        launch.FileName.ShouldBe(exe);
        launch.Args.ShouldBe(new[] { "status" });
        launch.RawArgumentLine.ShouldBeNull();
    }

    [Fact]
    public void Resolve_ReportsNoResolvedTargetWhenNothingOnPathMatches()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Feeds acceptance criterion 4: the caller can only name what was attempted if the seam
        // tells it whether anything was resolved at all.
        var launch = WindowsShimLaunch.Resolve("no-such-tool", ["--version"], _ => false);

        launch.ResolvedTarget.ShouldBeNull();
        launch.FileName.ShouldBe("no-such-tool");
    }

    [Fact]
    public void FormatLaunchFailureDetail_NamesResolvedShimPath()
    {
        var launch = new ProcessLaunch("pwsh.exe", ["-NoProfile", "-File", @"C:\shims\qmd.ps1"], null, @"C:\shims\qmd.ps1");

        var detail = launch.FormatLaunchFailureDetail("qmd");

        detail.ShouldContain(@"C:\shims\qmd.ps1");
        detail.ShouldContain("qmd");
    }

    [Fact]
    public void FormatLaunchFailureDetail_SaysNothingResolvedWhenPathProbeFailed()
    {
        var launch = new ProcessLaunch("qmd", ["--version"]);

        var detail = launch.FormatLaunchFailureDetail("qmd");

        detail.ShouldContain("qmd");
        detail.ShouldContain("PATH");
    }
}

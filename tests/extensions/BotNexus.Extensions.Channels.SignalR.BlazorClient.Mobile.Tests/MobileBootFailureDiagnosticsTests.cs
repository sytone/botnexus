using System;
using System.IO;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Tests;

/// <summary>
/// Issue #2880, AC6: the mobile client is NOT exempt from the boot-failure handler. It ships the
/// same static loading div and the same default-autostart <c>blazor.webassembly.js</c> tag as the
/// desktop portal, and is reached through the same authenticating reverse proxy, so a failed boot
/// there produces the identical indefinite "Loading..." with no on-screen cause.
///
/// The classifier logic itself is exercised under Node by
/// <c>BootFailureDiagnosticsTests</c> in the desktop test project. These tests pin the two things
/// that are specific to mobile: that its host page actually wires the handler up, and that its
/// copy of the script has not drifted from the desktop original -- a duplicated asset whose copies
/// diverge is the failure mode where one surface silently keeps the old broken behaviour.
/// </summary>
public sealed class MobileBootFailureDiagnosticsTests
{
    private static string RepoRelative(params string[] parts)
    {
        var head = new[]
        {
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
        };
        var all = new string[head.Length + parts.Length];
        head.CopyTo(all, 0);
        parts.CopyTo(all, head.Length);
        return Path.GetFullPath(Path.Combine(all));
    }

    private static readonly string MobileWwwroot = RepoRelative(
        "src", "extensions", "BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile", "wwwroot");

    private static readonly string DesktopWwwroot = RepoRelative(
        "src", "extensions", "BotNexus.Extensions.Channels.SignalR.BlazorClient", "wwwroot");

    [Fact]
    public void Mobile_ships_the_boot_diagnostics_script()
    {
        var path = Path.Combine(MobileWwwroot, "js", "bootDiagnostics.js");
        Assert.True(File.Exists(path), $"bootDiagnostics.js not found at {path}");
    }

    [Fact]
    public void Mobile_index_disables_autostart()
    {
        var content = File.ReadAllText(Path.Combine(MobileWwwroot, "index.html"));

        Assert.Matches("<script[^>]*blazor\\.webassembly[^>]*autostart=\"false\"", content);
    }

    [Fact]
    public void Mobile_index_loads_and_invokes_the_boot_handler()
    {
        var content = File.ReadAllText(Path.Combine(MobileWwwroot, "index.html"));

        Assert.Contains("js/bootDiagnostics.js", content, StringComparison.Ordinal);
        Assert.Contains("BotNexusBoot.startBlazor", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Mobile_boot_diagnostics_has_not_drifted_from_the_desktop_copy()
    {
        // The two clients are separate BlazorWebAssembly projects with separate wwwroot trees, so
        // the script is physically duplicated. Pinning byte equality is what stops a fix landing on
        // one surface and silently missing the other.
        var mobile = File.ReadAllText(Path.Combine(MobileWwwroot, "js", "bootDiagnostics.js"));
        var desktop = File.ReadAllText(Path.Combine(DesktopWwwroot, "js", "bootDiagnostics.js"));

        Assert.Equal(desktop, mobile);
    }

    [Fact]
    public void Mobile_error_reporting_exposes_the_shared_report_seam()
    {
        var content = File.ReadAllText(Path.Combine(MobileWwwroot, "js", "errorReporting.js"));

        Assert.Contains("BotNexusErrorReporting", content, StringComparison.Ordinal);
    }
}

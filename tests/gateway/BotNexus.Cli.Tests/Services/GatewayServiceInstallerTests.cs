using BotNexus.Cli.Services;
using Shouldly;

namespace BotNexus.Cli.Tests.Services;

/// <summary>
/// Unit tests for GatewayServiceInstaller content-generation methods (platform-independent).
/// These tests validate the generated service unit file / plist content without calling
/// the OS service manager or writing to privileged paths.
/// </summary>
public sealed class GatewayServiceInstallerTests
{
    // ── BuildSystemdUnit ────────────────────────────────────────────────────

    [Fact]
    public void BuildSystemdUnit_ContainsExecStartWithExecutable()
    {
        var unit = GatewayServiceInstaller.BuildSystemdUnit(
            executablePath: "/opt/botnexus/BotNexus.Gateway.Api.dll",
            homePath: "/home/user/.botnexus",
            port: 5005);

        unit.ShouldContain("/opt/botnexus/BotNexus.Gateway.Api.dll");
        unit.ShouldContain("--urls \"http://localhost:5005\"");
    }

    [Fact]
    public void BuildSystemdUnit_ContainsHomePath()
    {
        var unit = GatewayServiceInstaller.BuildSystemdUnit(
            executablePath: "/opt/botnexus/BotNexus.Gateway.Api.dll",
            homePath: "/home/alice/.botnexus",
            port: 5005);

        unit.ShouldContain("BOTNEXUS_HOME=/home/alice/.botnexus");
    }

    [Fact]
    public void BuildSystemdUnit_UsesCustomPort()
    {
        var unit = GatewayServiceInstaller.BuildSystemdUnit(
            executablePath: "/opt/botnexus/BotNexus.Gateway.Api.dll",
            homePath: "/home/user/.botnexus",
            port: 8080);

        unit.ShouldContain("http://localhost:8080");
        unit.ShouldNotContain("http://localhost:5005");
    }

    [Fact]
    public void BuildSystemdUnit_HasCorrectSections()
    {
        var unit = GatewayServiceInstaller.BuildSystemdUnit(
            executablePath: "/opt/botnexus/BotNexus.Gateway.Api.dll",
            homePath: "/home/user/.botnexus",
            port: 5005);

        unit.ShouldContain("[Unit]");
        unit.ShouldContain("[Service]");
        unit.ShouldContain("[Install]");
        unit.ShouldContain("WantedBy=multi-user.target");
        unit.ShouldContain("Restart=on-failure");
    }

    // ── BuildLaunchAgentPlist ───────────────────────────────────────────────

    [Fact]
    public void BuildLaunchAgentPlist_ContainsExecutableAndPort()
    {
        var plist = GatewayServiceInstaller.BuildLaunchAgentPlist(
            executablePath: "/Users/alice/botnexus/BotNexus.Gateway.Api.dll",
            homePath: "/Users/alice/.botnexus",
            port: 5005);

        plist.ShouldContain("/Users/alice/botnexus/BotNexus.Gateway.Api.dll");
        plist.ShouldContain("http://localhost:5005");
    }

    [Fact]
    public void BuildLaunchAgentPlist_ContainsHomePath()
    {
        var plist = GatewayServiceInstaller.BuildLaunchAgentPlist(
            executablePath: "/Users/alice/botnexus/BotNexus.Gateway.Api.dll",
            homePath: "/Users/alice/.botnexus",
            port: 5005);

        plist.ShouldContain("BOTNEXUS_HOME");
        plist.ShouldContain("/Users/alice/.botnexus");
    }

    [Fact]
    public void BuildLaunchAgentPlist_HasLabelAndRunAtLoad()
    {
        var plist = GatewayServiceInstaller.BuildLaunchAgentPlist(
            executablePath: "/usr/local/botnexus/BotNexus.Gateway.Api.dll",
            homePath: "/Users/user/.botnexus",
            port: 5005);

        plist.ShouldContain(GatewayServiceInstaller.LaunchAgentPlistName);
        plist.ShouldContain("<key>RunAtLoad</key>");
        plist.ShouldContain("<true/>");
        plist.ShouldContain("<key>KeepAlive</key>");
    }

    [Fact]
    public void BuildLaunchAgentPlist_IsValidXml()
    {
        var plist = GatewayServiceInstaller.BuildLaunchAgentPlist(
            executablePath: "/opt/botnexus/BotNexus.Gateway.Api.dll",
            homePath: "/Users/user/.botnexus",
            port: 5005);

        // Validate it's at least parseable XML
        var exception = Record.Exception(() =>
        {
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(plist);
        });
        exception.ShouldBeNull();
    }

    // ── InstallAsync argument validation ────────────────────────────────────

    [Fact]
    public async Task InstallAsync_EmptyExecutablePath_ReturnsFail()
    {
        var installer = new GatewayServiceInstaller();
        var result = await installer.InstallAsync(
            executablePath: "",
            homePath: "/home/.botnexus");

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("required");
    }

    [Fact]
    public async Task InstallAsync_MissingExecutable_ReturnsFail()
    {
        var installer = new GatewayServiceInstaller();
        var result = await installer.InstallAsync(
            executablePath: "/nonexistent/path/BotNexus.Gateway.Api.dll",
            homePath: "/home/.botnexus");

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
    }

    // ── ServiceInstallResult / ServiceInstallStatus record tests ────────────

    [Theory]
    [InlineData(true, "Test service installed.")]
    [InlineData(false, "Install failed: permission denied.")]
    public void ServiceInstallResult_RecordEquality(bool success, string message)
    {
        var a = new ServiceInstallResult(success, message);
        var b = new ServiceInstallResult(success, message);
        a.ShouldBe(b);
        a.Success.ShouldBe(success);
        a.Message.ShouldBe(message);
    }

    [Fact]
    public void ServiceInstallStatus_Installed_ReflectsState()
    {
        var status = new ServiceInstallStatus(IsInstalled: true, Platform: "linux", ServiceName: "botnexus");
        status.IsInstalled.ShouldBeTrue();
        status.Platform.ShouldBe("linux");
        status.ServiceName.ShouldBe("botnexus");
    }
}

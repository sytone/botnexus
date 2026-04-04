using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Tests.Deployment.Infrastructure;

namespace BotNexus.Tests.Deployment.Tests;

/// <summary>SC-DPL-001: First install — home directory structure created.</summary>
[Trait("Category", "Deployment")]
public sealed class FirstInstallTests
{
    [Fact]
    public async Task FirstLaunch_WithEmptyHome_CreatesDirectoryStructure()
    {
        var port = GatewayProcessFixture.FindFreePort();
        await using var gw = new GatewayProcessFixture(port);

        // Write appsettings.json but do NOT write config.json
        // — let BotNexusHome.Initialize() create the default structure
        gw.WriteAppSettings(GatewayProcessFixture.DefaultAppSettings(port));

        await gw.StartAsync();
        await gw.WaitForHealthyAsync();

        // Verify full directory structure created by BotNexusHome.Initialize()
        Directory.Exists(gw.BotNexusHome).Should().BeTrue("home directory should exist");
        Directory.Exists(Path.Combine(gw.BotNexusHome, "extensions")).Should().BeTrue("extensions/ should exist");
        Directory.Exists(Path.Combine(gw.BotNexusHome, "extensions", "providers")).Should().BeTrue("extensions/providers/ should exist");
        Directory.Exists(Path.Combine(gw.BotNexusHome, "extensions", "channels")).Should().BeTrue("extensions/channels/ should exist");
        Directory.Exists(Path.Combine(gw.BotNexusHome, "extensions", "tools")).Should().BeTrue("extensions/tools/ should exist");
        Directory.Exists(Path.Combine(gw.BotNexusHome, "tokens")).Should().BeTrue("tokens/ should exist");
        Directory.Exists(Path.Combine(gw.BotNexusHome, "sessions")).Should().BeTrue("sessions/ should exist");
        Directory.Exists(Path.Combine(gw.BotNexusHome, "logs")).Should().BeTrue("logs/ should exist");
        Directory.Exists(Path.Combine(gw.BotNexusHome, "agents")).Should().BeTrue("agents/ should exist");

        // Verify config.json was auto-generated
        File.Exists(gw.ConfigJsonPath).Should().BeTrue("config.json should be auto-created on first install");
        var configContent = await File.ReadAllTextAsync(gw.ConfigJsonPath);
        configContent.Should().Contain("BotNexus", "config.json should contain BotNexus section");
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Tests.Deployment.Infrastructure;

namespace BotNexus.Tests.Deployment.Tests;

/// <summary>SC-DPL-006: Add extension → restart → verify extension loaded.</summary>
[Trait("Category", "Deployment")]
public sealed class ExtensionAddTests
{
    [Fact]
    public async Task AddExtension_AfterRestart_ExtensionLoaded()
    {
        var port = GatewayProcessFixture.FindFreePort();
        await using var gw = new GatewayProcessFixture(port);

        gw.WriteAppSettings(GatewayProcessFixture.DefaultAppSettings(port));
        gw.WriteConfigJson(GatewayProcessFixture.MinimalConfigJson());

        // --- Start without extensions ---
        await gw.StartAsync();
        await gw.WaitForHealthyAsync();

        using var client1 = gw.CreateHttpClient();
        var ext1 = await client1.GetFromJsonAsync<JsonElement>("/api/extensions");
        var loadedBefore = ext1.GetProperty("loaded").GetInt32();

        await gw.StopAsync();

        // --- Deploy extension DLL + configure it in config.json ---
        if (!File.Exists(GatewayProcessFixture.ExtensionConventionDllPath))
            return;

        gw.DeployExtension("tools", "convention-echo", GatewayProcessFixture.ExtensionConventionDllPath);

        // Extension loader only discovers extensions listed in config
        gw.WriteConfigJson("""
            {
              "BotNexus": {
                "Tools": {
                  "Extensions": {
                    "convention-echo": {}
                  }
                }
              }
            }
            """);

        // --- Restart ---
        await gw.StartAsync();
        await gw.WaitForHealthyAsync();

        using var client2 = gw.CreateHttpClient();
        var ext2 = await client2.GetFromJsonAsync<JsonElement>("/api/extensions");
        var loadedAfter = ext2.GetProperty("loaded").GetInt32();

        loadedAfter.Should().BeGreaterThan(loadedBefore,
            "extension count should increase after deploying a new extension");

        var results = ext2.GetProperty("results").EnumerateArray().ToList();
        results.Should().Contain(r =>
            r.GetProperty("key").GetString() == "convention-echo" &&
            r.GetProperty("success").GetBoolean(),
            "convention-echo extension should load successfully");
    }
}

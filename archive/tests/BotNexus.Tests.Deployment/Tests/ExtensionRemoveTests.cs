using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Tests.Deployment.Infrastructure;

namespace BotNexus.Tests.Deployment.Tests;

/// <summary>SC-DPL-007: Remove extension → restart → verify extension gone.</summary>
[Trait("Category", "Deployment")]
public sealed class ExtensionRemoveTests
{
    [Fact]
    public async Task RemoveExtension_AfterRestart_ExtensionGone()
    {
        var port = GatewayProcessFixture.FindFreePort();
        await using var gw = new GatewayProcessFixture(port);

        if (!File.Exists(GatewayProcessFixture.ExtensionConventionDllPath))
            return;

        // --- Deploy extension, configure it, and start ---
        gw.WriteAppSettings(GatewayProcessFixture.DefaultAppSettings(port));
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
        gw.DeployExtension("tools", "convention-echo", GatewayProcessFixture.ExtensionConventionDllPath);

        await gw.StartAsync();
        await gw.WaitForHealthyAsync();

        using var client1 = gw.CreateHttpClient();
        var ext1 = await client1.GetFromJsonAsync<JsonElement>("/api/extensions");
        var loadedBefore = ext1.GetProperty("loaded").GetInt32();

        var results1 = ext1.GetProperty("results").EnumerateArray().ToList();
        results1.Should().Contain(r =>
            r.GetProperty("key").GetString() == "convention-echo",
            "extension should be loaded initially");

        await gw.StopAsync();

        // --- Remove extension folder AND config ---
        gw.RemoveExtension("tools", "convention-echo");
        gw.WriteConfigJson(GatewayProcessFixture.MinimalConfigJson());

        // --- Restart ---
        await gw.StartAsync();
        await gw.WaitForHealthyAsync();

        using var client2 = gw.CreateHttpClient();
        var ext2 = await client2.GetFromJsonAsync<JsonElement>("/api/extensions");
        var loadedAfter = ext2.GetProperty("loaded").GetInt32();

        loadedAfter.Should().BeLessThan(loadedBefore,
            "extension count should decrease after removing an extension");

        var results2 = ext2.GetProperty("results").EnumerateArray().ToList();
        results2.Should().NotContain(r =>
            r.GetProperty("key").GetString() == "convention-echo",
            "convention-echo should not be present after removal");
    }
}

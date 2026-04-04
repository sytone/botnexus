using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Tests.Deployment.Infrastructure;

namespace BotNexus.Tests.Deployment.Tests;

/// <summary>SC-DPL-008: Config change → restart → verify new config applied.</summary>
[Trait("Category", "Deployment")]
public sealed class ConfigChangeTests
{
    [Fact]
    public async Task ConfigChange_AfterRestart_NewConfigApplied()
    {
        var port = GatewayProcessFixture.FindFreePort();
        await using var gw = new GatewayProcessFixture(port);

        gw.WriteAppSettings(GatewayProcessFixture.DefaultAppSettings(port));

        // Initial config: one agent "alpha"
        gw.WriteConfigJson("""
            {
              "BotNexus": {
                "Agents": {
                  "Named": {
                    "alpha": { "Name": "alpha", "SystemPrompt": "You are Alpha." }
                  }
                }
              }
            }
            """);

        // --- First start ---
        await gw.StartAsync();
        await gw.WaitForHealthyAsync();

        using var client1 = gw.CreateHttpClient();
        var agents1 = await client1.GetFromJsonAsync<JsonElement>("/api/agents");
        var names1 = agents1.EnumerateArray()
            .Select(a => a.GetProperty("name").GetString())
            .ToList();

        names1.Should().Contain("alpha");
        names1.Should().NotContain("beta");

        await gw.StopAsync();

        // --- Modify config: add "beta" ---
        gw.WriteConfigJson("""
            {
              "BotNexus": {
                "Agents": {
                  "Named": {
                    "alpha": { "Name": "alpha", "SystemPrompt": "You are Alpha." },
                    "beta": { "Name": "beta", "SystemPrompt": "You are Beta." }
                  }
                }
              }
            }
            """);

        // --- Restart ---
        await gw.StartAsync();
        await gw.WaitForHealthyAsync();

        using var client2 = gw.CreateHttpClient();
        var agents2 = await client2.GetFromJsonAsync<JsonElement>("/api/agents");
        var names2 = agents2.EnumerateArray()
            .Select(a => a.GetProperty("name").GetString())
            .ToList();

        names2.Should().Contain("alpha", "original agent should still be present");
        names2.Should().Contain("beta", "new agent from config change should appear");
    }
}

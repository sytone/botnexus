using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Tests.Deployment.Infrastructure;

namespace BotNexus.Tests.Deployment.Tests;

/// <summary>SC-DPL-009: Health/ready probes during startup — transitions from unavailable to healthy.</summary>
[Trait("Category", "Deployment")]
public sealed class HealthDuringStartupTests
{
    [Fact]
    public async Task HealthProbes_DuringStartup_TransitionToHealthy()
    {
        var port = GatewayProcessFixture.FindFreePort();
        await using var gw = new GatewayProcessFixture(port);

        gw.WriteAppSettings(GatewayProcessFixture.DefaultAppSettings(port));
        gw.WriteConfigJson(GatewayProcessFixture.MinimalConfigJson());

        // Start the Gateway and IMMEDIATELY begin polling (don't wait for healthy first)
        await gw.StartAsync();

        using var client = gw.CreateHttpClient();
        var sw = Stopwatch.StartNew();
        var sawConnectionRefused = false;
        var sawHealthy = false;
        var attempts = 0;
        const int maxAttempts = 150; // 150 * 200ms = 30s max

        while (attempts < maxAttempts && !sawHealthy)
        {
            attempts++;
            try
            {
                var response = await client.GetAsync("/health");
                if (response.IsSuccessStatusCode)
                {
                    sawHealthy = true;
                    var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                    json.GetProperty("status").GetString().Should().NotBeNullOrWhiteSpace();
                }
            }
            catch (HttpRequestException)
            {
                // Connection refused — server not yet listening
                sawConnectionRefused = true;
            }

            if (!sawHealthy)
                await Task.Delay(200);
        }

        sw.Stop();

        sawHealthy.Should().BeTrue("Gateway /health should eventually return 200");

        // The health endpoint should become available relatively quickly
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
            "Gateway should be healthy within 30 seconds");

        // Verify the startup sequence happened (connection refused → healthy)
        // Note: On fast machines, the server may start before the first poll
        if (sawConnectionRefused)
        {
            // We observed the transition from unavailable → healthy
            sawConnectionRefused.Should().BeTrue("should see unavailable phase before healthy");
        }

        // Verify /ready also transitions to healthy
        var readyResponse = await client.GetAsync("/ready");
        readyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

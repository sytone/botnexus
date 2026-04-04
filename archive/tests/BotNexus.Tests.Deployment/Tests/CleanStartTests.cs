using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Tests.Deployment.Infrastructure;

namespace BotNexus.Tests.Deployment.Tests;

/// <summary>SC-DPL-002: Clean Gateway start — /health returns 200, /ready returns 200.</summary>
[Trait("Category", "Deployment")]
public sealed class CleanStartTests
{
    [Fact]
    public async Task CleanStart_HealthAndReady_Return200()
    {
        var port = GatewayProcessFixture.FindFreePort();
        await using var gw = new GatewayProcessFixture(port);

        // Pre-create config.json to ensure deterministic test config
        gw.WriteAppSettings(GatewayProcessFixture.DefaultAppSettings(port));
        gw.WriteConfigJson(GatewayProcessFixture.MinimalConfigJson());

        await gw.StartAsync();
        await gw.WaitForHealthyAsync();

        using var client = gw.CreateHttpClient();

        // Verify /health returns 200 with valid JSON body
        var healthResponse = await client.GetAsync("/health");
        healthResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var healthJson = await healthResponse.Content.ReadFromJsonAsync<JsonElement>();
        healthJson.GetProperty("status").GetString().Should().NotBeNullOrWhiteSpace();
        healthJson.GetProperty("checks").ValueKind.Should().Be(JsonValueKind.Object);
        healthJson.GetProperty("totalDuration").GetDouble().Should().BeGreaterThanOrEqualTo(0);

        // Verify /ready returns 200 (no configured providers/channels = all healthy)
        var readyResponse = await client.GetAsync("/ready");
        readyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var readyJson = await readyResponse.Content.ReadFromJsonAsync<JsonElement>();
        readyJson.GetProperty("status").GetString().Should().Be("Healthy");
    }
}

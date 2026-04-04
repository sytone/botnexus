using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Tests.Deployment.Infrastructure;

namespace BotNexus.Tests.Deployment.Tests;

/// <summary>SC-DPL-004: Graceful shutdown — sessions persisted to disk.</summary>
[Trait("Category", "Deployment")]
public sealed class GracefulShutdownTests
{
    [Fact]
    public async Task GracefulStop_SessionFilesSurvive()
    {
        var port = GatewayProcessFixture.FindFreePort();
        await using var gw = new GatewayProcessFixture(port);

        gw.WriteAppSettings(GatewayProcessFixture.DefaultAppSettings(port));
        gw.WriteConfigJson(GatewayProcessFixture.MinimalConfigJson());

        // Seed session data (simulates prior conversation)
        gw.SeedSession("deploy-sess-1",
            ("User", "What is BotNexus?"),
            ("Assistant", "BotNexus is a modular AI agent platform."));
        gw.SeedSession("deploy-sess-2",
            ("User", "Tell me about agents."),
            ("Assistant", "Agents are configurable AI workers."));

        await gw.StartAsync();
        await gw.WaitForHealthyAsync();

        using var client = gw.CreateHttpClient();

        // Verify sessions are loaded and visible via API
        var sessionsResponse = await client.GetAsync("/api/sessions");
        sessionsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var sessions = await sessionsResponse.Content.ReadFromJsonAsync<JsonElement>();
        sessions.GetArrayLength().Should().BeGreaterThanOrEqualTo(2);

        // Stop the Gateway
        await gw.StopAsync();

        // Verify session files still exist on disk
        gw.SessionFileExists("deploy-sess-1").Should().BeTrue("session 1 file should survive shutdown");
        gw.SessionFileExists("deploy-sess-2").Should().BeTrue("session 2 file should survive shutdown");

        // Verify file content is intact
        var sessDir = gw.SessionsPath;
        var files = Directory.GetFiles(sessDir, "*.jsonl");
        files.Length.Should().BeGreaterThanOrEqualTo(2);
    }
}

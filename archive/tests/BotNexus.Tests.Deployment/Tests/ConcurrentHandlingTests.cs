using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BotNexus.Tests.Deployment.Infrastructure;

namespace BotNexus.Tests.Deployment.Tests;

/// <summary>SC-DPL-010: Concurrent message handling — Gateway handles parallel requests without failures.</summary>
[Trait("Category", "Deployment")]
public sealed class ConcurrentHandlingTests
{
    [Fact]
    public async Task ConcurrentRequests_AllSucceed_NoErrors()
    {
        var port = GatewayProcessFixture.FindFreePort();
        await using var gw = new GatewayProcessFixture(port);

        gw.WriteAppSettings(GatewayProcessFixture.DefaultAppSettings(port));
        gw.WriteConfigJson(GatewayProcessFixture.MinimalConfigJson());

        await gw.StartAsync();
        await gw.WaitForHealthyAsync();

        const int concurrency = 50;

        // --- Concurrent health checks ---
        var healthTasks = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            using var client = gw.CreateHttpClient();
            var response = await client.GetAsync("/health");
            return response.StatusCode;
        });

        var healthResults = await Task.WhenAll(healthTasks);
        healthResults.Should().AllBeEquivalentTo(HttpStatusCode.OK,
            "all concurrent health checks should return 200");

        // --- Concurrent API requests (mixed endpoints) ---
        var apiTasks = Enumerable.Range(0, concurrency).Select(async i =>
        {
            using var client = gw.CreateHttpClient();
            var endpoint = (i % 4) switch
            {
                0 => "/api/agents",
                1 => "/api/sessions",
                2 => "/api/extensions",
                _ => "/health"
            };
            var response = await client.GetAsync(endpoint);
            return (endpoint, response.StatusCode);
        });

        var apiResults = await Task.WhenAll(apiTasks);
        foreach (var (endpoint, statusCode) in apiResults)
        {
            statusCode.Should().Be(HttpStatusCode.OK,
                $"concurrent request to {endpoint} should return 200");
        }

        // --- Concurrent WebSocket connections ---
        var wsTasks = Enumerable.Range(0, 10).Select(async i =>
        {
            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            try
            {
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), cts.Token);

                // Read the "connected" message
                var buffer = new byte[4096];
                var result = await ws.ReceiveAsync(buffer, cts.Token);
                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var json = JsonDocument.Parse(message);
                var type = json.RootElement.GetProperty("type").GetString();

                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                return (success: true, type);
            }
            catch (Exception ex)
            {
                return (success: false, type: ex.Message);
            }
        });

        var wsResults = await Task.WhenAll(wsTasks);
        var successfulWs = wsResults.Count(r => r.success);
        successfulWs.Should().Be(10, "all WebSocket connections should succeed");
        wsResults.Where(r => r.success).Should().AllSatisfy(r =>
            r.type.Should().Be("connected", "each connection should receive 'connected' message"));

        // --- Verify no errors in Gateway logs ---
        var errorLines = gw.Stderr.Where(l =>
            l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("exception", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Allow some warnings but no hard errors
        var criticalErrors = errorLines.Where(l =>
            l.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("fatal", StringComparison.OrdinalIgnoreCase))
            .ToList();

        criticalErrors.Should().BeEmpty(
            "no critical errors should occur during concurrent operation");
    }
}

using BotNexus.Probe.Gateway;
using System.Text.Json;

namespace BotNexus.Probe.Api;

public static class GatewayEndpoints
{
    public static IEndpointRouteBuilder MapGatewayEndpoints(
        this IEndpointRouteBuilder app,
        GatewayClient? gatewayClient,
        GatewayHubClient? gatewayHubClient)
    {
        app.MapGet("/api/gateway/status", async (CancellationToken cancellationToken) =>
        {
            if (gatewayClient is null)
            {
                return Results.Ok(new { configured = false, connected = false, healthy = false, message = "Gateway URL not configured." });
            }

            var health = await gatewayClient.CheckHealthAsync(cancellationToken);
            return Results.Ok(new
            {
                configured = true,
                connected = gatewayHubClient?.IsConnected ?? false,
                healthy = health.Healthy,
                reachable = health.Reachable,
                health.StatusCode,
                health.Payload,
                hubError = gatewayHubClient?.LastError
            });
        });

        app.MapGet("/api/gateway/logs", async (int? limit, CancellationToken cancellationToken) =>
            await ProxyJsonAsync(
                gatewayClient is null
                    ? null
                    : await gatewayClient.GetRecentLogsAsync(Math.Clamp(limit ?? 100, 1, 1_000), cancellationToken)));

        app.MapGet("/api/gateway/sessions", async (CancellationToken cancellationToken) =>
            await ProxyJsonAsync(gatewayClient is null ? null : await gatewayClient.GetSessionsAsync(cancellationToken)));

        app.MapGet("/api/gateway/agents", async (CancellationToken cancellationToken) =>
            await ProxyJsonAsync(gatewayClient is null ? null : await gatewayClient.GetAgentsAsync(cancellationToken)));

        app.MapGet("/api/gateway/activity", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (gatewayHubClient is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("Gateway hub client is not configured.", cancellationToken);
                return;
            }

            context.Response.Headers.Append("Content-Type", "text/event-stream");
            context.Response.Headers.Append("Cache-Control", "no-cache");

            await foreach (var activity in gatewayHubClient.ReadActivityAsync(cancellationToken))
            {
                var payload = JsonSerializer.Serialize(activity);
                await context.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        });

        return app;
    }

    private static Task<IResult> ProxyJsonAsync(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Task.FromResult<IResult>(
                Results.Problem("Gateway unavailable or returned no data.", statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        return Task.FromResult<IResult>(Results.Content(payload, "application/json"));
    }
}

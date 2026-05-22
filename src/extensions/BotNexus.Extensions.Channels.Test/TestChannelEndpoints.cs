using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// Minimal API endpoints for the test channel HTTP interface.
/// Map these endpoints in your test host startup via <see cref="MapTestChannelEndpoints"/>.
/// </summary>
public static class TestChannelEndpoints
{
    /// <summary>
    /// Maps the test channel HTTP endpoints to the given route builder.
    /// </summary>
    /// <remarks>
    /// Endpoints:
    /// <list type="bullet">
    ///   <item><c>POST /test-channel/{channelId}/inbound</c> — inject inbound message</item>
    ///   <item><c>GET /test-channel/{channelId}/outbound</c> — poll outbound messages</item>
    ///   <item><c>DELETE /test-channel/{channelId}/outbound</c> — clear outbound queue</item>
    ///   <item><c>GET /test-channel/logs</c> — get captured log entries</item>
    ///   <item><c>DELETE /test-channel/logs</c> — clear log buffer</item>
    /// </list>
    /// </remarks>
    public static IEndpointRouteBuilder MapTestChannelEndpoints(
        this IEndpointRouteBuilder app,
        TestChannelAdapter adapter)
    {
        var group = app.MapGroup("/test-channel");

        // POST /test-channel/{channelId}/inbound
        group.MapPost("{channelId}/inbound", async (string channelId, InboundMessageRequest req, CancellationToken ct) =>
        {
            await adapter.InjectInboundAsync(channelId, req.Content, req.SenderId, req.TargetAgentId, ct);
            return Results.Ok(new { channelId, injected = true });
        });

        // GET /test-channel/{channelId}/outbound
        group.MapGet("{channelId}/outbound", (string channelId) =>
        {
            var messages = adapter.GetOutbound(channelId);
            return Results.Ok(messages.Select(m => new
            {
                channelAddress = m.ChannelAddress.Value,
                content = m.Content,
                metadata = m.Metadata
            }));
        });

        // DELETE /test-channel/{channelId}/outbound
        group.MapDelete("{channelId}/outbound", (string channelId) =>
        {
            adapter.ClearOutbound(channelId);
            return Results.NoContent();
        });

        // GET /test-channel/logs
        group.MapGet("logs", () =>
        {
            var logs = adapter.GetLogs();
            return Results.Ok(logs.Select(l => new
            {
                timestamp = l.Timestamp,
                level = l.Level,
                message = l.Message,
                properties = l.Properties
            }));
        });

        // DELETE /test-channel/logs
        group.MapDelete("logs", () =>
        {
            adapter.ClearLogs();
            return Results.NoContent();
        });

        return app;
    }

    /// <summary>
    /// Request body for injecting an inbound message.
    /// </summary>
    public sealed record InboundMessageRequest(
        string Content,
        string SenderId,
        string? TargetAgentId = null);
}

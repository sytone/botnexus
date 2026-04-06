using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Activity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Api.WebSocket;

/// <summary>
/// Streams gateway activity events to WebSocket subscribers.
/// </summary>
public sealed class ActivityWebSocketHandler(
    IActivityBroadcaster broadcaster,
    ILogger<ActivityWebSocketHandler> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

/// <summary>
/// Handles WebSocket connections and streams gateway activity events to connected clients.
/// Supports optional agent filtering via query parameter.
/// </summary>
/// <param name="context">The HTTP context containing the WebSocket request.</param>
/// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
/// <returns>A task that represents the asynchronous WebSocket handling operation.</returns>
public async Task HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var agentFilter = context.Request.Query["agent"].FirstOrDefault();
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        logger.LogInformation("Activity WebSocket connected (agent filter: {AgentFilter})", agentFilter ?? "*");

        try
        {
            await foreach (var activity in broadcaster.SubscribeAsync(cancellationToken))
            {
                if (socket.State != WebSocketState.Open)
                    break;

                if (!string.IsNullOrWhiteSpace(agentFilter) &&
                    !string.Equals(activity.AgentId, agentFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var payload = JsonSerializer.SerializeToUtf8Bytes(activity, JsonOptions);
                await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Activity WebSocket cancelled");
        }
        finally
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Activity stream closed", CancellationToken.None);
            }
        }
    }
}

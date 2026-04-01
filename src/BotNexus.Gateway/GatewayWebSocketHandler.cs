using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway;

/// <summary>
/// Handles individual WebSocket connections on the gateway's <c>/ws</c> endpoint.
/// Inbound text messages are published to <see cref="IMessageBus"/>; outbound responses
/// arrive via <see cref="WebSocketChannel"/> and are forwarded back to the socket.
/// </summary>
/// <remarks>
/// Message format (client → gateway):
/// <code>{"type":"message","content":"hello","session_id":"optional-override"}</code>
/// Message format (gateway → client):
/// <code>{"type":"connected","connection_id":"abc123"}</code>
/// <code>{"type":"response","content":"agent reply"}</code>
/// <code>{"type":"delta","content":"streaming token"}</code>
/// </remarks>
public sealed class GatewayWebSocketHandler
{
    private readonly IMessageBus _messageBus;
    private readonly WebSocketChannel _wsChannel;
    private readonly ILogger<GatewayWebSocketHandler> _logger;

    public GatewayWebSocketHandler(
        IMessageBus messageBus,
        WebSocketChannel wsChannel,
        ILogger<GatewayWebSocketHandler> logger)
    {
        _messageBus = messageBus;
        _wsChannel = wsChannel;
        _logger = logger;
    }

    /// <summary>Handles a single WebSocket connection until it closes.</summary>
    public async Task HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var connectionId = Guid.NewGuid().ToString("N");
        _logger.LogInformation("WebSocket client connected: {ConnectionId}", connectionId);

        var responseReader = _wsChannel.AddConnection(connectionId);

        // Notify client of its assigned connection id
        await SendJsonAsync(socket, new WsConnectedMessage(connectionId), cancellationToken).ConfigureAwait(false);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linkedCts.Token;

        var readerTask = ReadFromClientAsync(socket, connectionId, token);
        var writerTask = WriteToClientAsync(socket, responseReader, token);

        // Stop both loops as soon as either side finishes (disconnect or shutdown)
        await Task.WhenAny(readerTask, writerTask).ConfigureAwait(false);
        await linkedCts.CancelAsync().ConfigureAwait(false);

        _wsChannel.RemoveConnection(connectionId);

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // Socket may already be closing
            }
        }

        _logger.LogInformation("WebSocket client disconnected: {ConnectionId}", connectionId);
    }

    private async Task ReadFromClientAsync(WebSocket socket, string connectionId, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            try
            {
                var text = await ReceiveTextAsync(socket, buffer, ct).ConfigureAwait(false);
                if (text is null) break; // close frame received

                var inbound = ParseInbound(text, connectionId);
                if (inbound is null) continue;

                await _messageBus.PublishAsync(inbound, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                _logger.LogDebug(ex, "WebSocket error reading from {ConnectionId}", connectionId);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error reading from WebSocket {ConnectionId}", connectionId);
                break;
            }
        }
    }

    private static async Task WriteToClientAsync(
        WebSocket socket,
        ChannelReader<string> reader,
        CancellationToken ct)
    {
        await foreach (var json in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (socket.State != WebSocketState.Open) break;
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(
                bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private InboundMessage? ParseInbound(string text, string connectionId)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<WsInboundMessage>(text);
            if (dto?.Content is null)
            {
                _logger.LogDebug("Received WebSocket message without content from {ConnectionId}", connectionId);
                return null;
            }

            return new InboundMessage(
                Channel: "websocket",
                SenderId: connectionId,
                ChatId: connectionId,
                Content: dto.Content,
                Timestamp: DateTimeOffset.UtcNow,
                Media: [],
                Metadata: new Dictionary<string, object>(),
                SessionKeyOverride: string.IsNullOrEmpty(dto.SessionId) ? null : dto.SessionId);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Invalid JSON from WebSocket {ConnectionId}", connectionId);
            return null;
        }
    }

    private static Task SendJsonAsync<T>(WebSocket socket, T value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value);
        var bytes = Encoding.UTF8.GetBytes(json);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }
}

/// <summary>JSON message received from a WebSocket client.</summary>
internal sealed record WsInboundMessage(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("session_id")] string? SessionId);

/// <summary>Initial "connected" event sent to a WebSocket client on connection.</summary>
internal sealed record WsConnectedMessage(
    [property: JsonPropertyName("connection_id")] string ConnectionId)
{
    [JsonPropertyName("type")]
    public string Type => "connected";
}

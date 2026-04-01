using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway;

/// <summary>
/// An <see cref="IChannel"/> that routes agent responses back to connected WebSocket clients.
/// Each active connection has its own response queue that is drained by the WebSocket write loop.
/// Also publishes outbound events to the activity stream so the web UI can monitor all traffic.
/// </summary>
public sealed class WebSocketChannel : IChannel
{
    private readonly ConcurrentDictionary<string, Channel<string>> _connections = new();
    private readonly IActivityStream _activityStream;
    private readonly ILogger<WebSocketChannel> _logger;

    public WebSocketChannel(IActivityStream activityStream, ILogger<WebSocketChannel> logger)
    {
        _activityStream = activityStream;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "websocket";

    /// <inheritdoc/>
    public string DisplayName => "WebSocket";

    /// <inheritdoc/>
    public bool IsRunning { get; private set; }

    /// <inheritdoc/>
    public bool SupportsStreaming => true;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        IsRunning = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        IsRunning = false;
        foreach (var (_, ch) in _connections)
            ch.Writer.TryComplete();
        _connections.Clear();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public bool IsAllowed(string senderId) => true;

    /// <summary>
    /// Registers a new WebSocket connection and returns the reader for its response queue.
    /// </summary>
    public ChannelReader<string> AddConnection(string connectionId)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _connections[connectionId] = channel;
        _logger.LogDebug("WebSocket connection {ConnectionId} registered", connectionId);
        return channel.Reader;
    }

    /// <summary>
    /// Removes a connection and completes its response queue.
    /// </summary>
    public void RemoveConnection(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var ch))
        {
            ch.Writer.TryComplete();
            _logger.LogDebug("WebSocket connection {ConnectionId} removed", connectionId);
        }
    }

    /// <inheritdoc/>
    public async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(message.ChatId, out var ch))
        {
            _logger.LogDebug("WebSocket connection {ChatId} not found, dropping response", message.ChatId);
            return;
        }

        var json = JsonSerializer.Serialize(new WsOutboundMessage("response", message.Content));
        ch.Writer.TryWrite(json);

        // Broadcast to activity stream
        await _activityStream.PublishAsync(new ActivityEvent(
            ActivityEventType.ResponseSent,
            "websocket",
            $"websocket:{message.ChatId}",
            message.ChatId,
            null,
            message.Content,
            DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SendDeltaAsync(string chatId, string delta,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(chatId, out var ch))
            return;

        var json = JsonSerializer.Serialize(new WsOutboundMessage("delta", delta));
        ch.Writer.TryWrite(json);

        await _activityStream.PublishAsync(new ActivityEvent(
            ActivityEventType.DeltaSent,
            "websocket",
            $"websocket:{chatId}",
            chatId,
            null,
            delta,
            DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>JSON message sent from the gateway to a WebSocket client.</summary>
internal sealed record WsOutboundMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("content")] string Content);

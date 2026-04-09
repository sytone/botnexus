using System.Collections.Concurrent;
using System.Text.Json;
using BotNexus.Extensions.Mcp.Protocol;
using BotNexus.Extensions.Mcp.Transport;

namespace BotNexus.Extensions.Mcp.Tests;

/// <summary>
/// In-memory transport for testing McpClient without spawning a real process.
/// </summary>
internal sealed class MockMcpTransport : IMcpTransport
{
    private readonly ConcurrentQueue<JsonRpcResponse> _responseQueue = new();
    private readonly SemaphoreSlim _responseSemaphore = new(0);
    private readonly List<JsonRpcRequest> _sentRequests = [];
    private readonly List<JsonRpcNotification> _sentNotifications = [];
    private bool _connected;

    /// <summary>All requests sent through this transport.</summary>
    public IReadOnlyList<JsonRpcRequest> SentRequests => _sentRequests;

    /// <summary>All notifications sent through this transport.</summary>
    public IReadOnlyList<JsonRpcNotification> SentNotifications => _sentNotifications;

    /// <summary>
    /// Enqueue a response for the next ReceiveAsync call.
    /// </summary>
    public void EnqueueResponse(JsonRpcResponse response)
    {
        _responseQueue.Enqueue(response);
        _responseSemaphore.Release();
    }

    /// <summary>
    /// Enqueue a successful response with the given result.
    /// </summary>
    public void EnqueueResult(object id, object result)
    {
        EnqueueResponse(new JsonRpcResponse
        {
            Id = id,
            Result = JsonSerializer.SerializeToElement(result),
        });
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    public Task SendAsync(JsonRpcRequest message, CancellationToken ct = default)
    {
        if (!_connected) throw new InvalidOperationException("Not connected.");
        _sentRequests.Add(message);
        return Task.CompletedTask;
    }

    public Task SendNotificationAsync(JsonRpcNotification message, CancellationToken ct = default)
    {
        if (!_connected) throw new InvalidOperationException("Not connected.");
        _sentNotifications.Add(message);
        return Task.CompletedTask;
    }

    public async Task<JsonRpcResponse> ReceiveAsync(CancellationToken ct = default)
    {
        await _responseSemaphore.WaitAsync(ct);
        _responseQueue.TryDequeue(out var response);
        return response!;
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _connected = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        return ValueTask.CompletedTask;
    }
}

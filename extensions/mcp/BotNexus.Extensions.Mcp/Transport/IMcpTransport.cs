using BotNexus.Extensions.Mcp.Protocol;

namespace BotNexus.Extensions.Mcp.Transport;

/// <summary>
/// Abstraction for MCP transport — the channel for sending and receiving JSON-RPC messages.
/// </summary>
public interface IMcpTransport : IAsyncDisposable
{
    /// <summary>Connect to the MCP server.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Send a JSON-RPC request to the server.</summary>
    Task SendAsync(JsonRpcRequest message, CancellationToken ct = default);

    /// <summary>Send a JSON-RPC notification to the server.</summary>
    Task SendNotificationAsync(JsonRpcNotification message, CancellationToken ct = default);

    /// <summary>Receive a JSON-RPC response from the server.</summary>
    Task<JsonRpcResponse> ReceiveAsync(CancellationToken ct = default);

    /// <summary>Disconnect from the MCP server.</summary>
    Task DisconnectAsync(CancellationToken ct = default);
}

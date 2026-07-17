using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace BotNexus.Agent.Providers.Copilot.Responses;

internal enum CopilotResponsesTransportPreference
{
    Auto,
    Sse,
    WebSocket
}

internal enum CopilotResponsesWireTransport
{
    Sse,
    WebSocket
}

internal static class CopilotResponsesTransportPolicy
{
    internal static CopilotResponsesWireTransport Select(
        BotNexus.Agent.Providers.Core.Models.LlmModel model,
        CopilotResponsesTransportPreference preference)
        => preference switch
        {
            CopilotResponsesTransportPreference.Sse => CopilotResponsesWireTransport.Sse,
            CopilotResponsesTransportPreference.WebSocket => CopilotResponsesWireTransport.WebSocket,
            _ when CopilotResolvedModelDescriptors.Get(model).SupportsResponsesWebSocket => CopilotResponsesWireTransport.WebSocket,
            _ => CopilotResponsesWireTransport.Sse
        };
}

internal interface ICopilotResponsesWebSocketTransport : IAsyncDisposable
{
    ValueTask ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);
    ValueTask SendAsync(string payload, CancellationToken cancellationToken);
    ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken);
}

internal sealed class CopilotResponsesWebSocketTransport : ICopilotResponsesWebSocketTransport
{
    private const int MaxMessageBytes = 16 * 1024 * 1024;
    private readonly ClientWebSocket _socket = new();

    public async ValueTask ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        foreach (var (key, value) in headers)
            _socket.Options.SetRequestHeader(key, value);
        await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask SendAsync(string payload, CancellationToken cancellationToken)
        => new(_socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, cancellationToken));

    public async ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var memory = writer.GetMemory(8192);
            var result = await _socket.ReceiveAsync(memory, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("Copilot Responses WebSocket returned a non-text message.");
            writer.Advance(result.Count);
            if (writer.WrittenCount > MaxMessageBytes)
                throw new InvalidDataException($"Copilot Responses WebSocket message exceeded {MaxMessageBytes} bytes.");
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(writer.WrittenSpan);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "complete", CancellationToken.None).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
            }
        }
        _socket.Dispose();
    }
}

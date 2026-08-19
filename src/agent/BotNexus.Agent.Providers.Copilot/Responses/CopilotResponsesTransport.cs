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

/// <summary>
/// The close status and server-supplied reason observed on a WebSocket close frame.
/// </summary>
/// <remarks>
/// Captured so a fallback can say <em>why</em> the server hung up (#3366). A 1009 (message too big)
/// and a 1011 (internal error) are operationally different problems; collapsing both into a bare
/// <see langword="null"/> receive made them indistinguishable in logs and telemetry.
/// </remarks>
/// <param name="Code">Numeric WebSocket close code, or <see langword="null"/> if the peer sent none.</param>
/// <param name="Reason">Server-supplied close description; <see langword="null"/> or empty when absent.</param>
internal sealed record CopilotResponsesCloseFrame(int? Code, string? Reason)
{
    /// <summary>Renders the close evidence for a log line, exception message, or activity tag.</summary>
    public string Describe()
    {
        var code = Code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        return string.IsNullOrWhiteSpace(Reason) ? $"close code {code} (no reason supplied)" : $"close code {code}: {Reason}";
    }
}

internal interface ICopilotResponsesWebSocketTransport : IAsyncDisposable
{
    ValueTask ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);
    ValueTask SendAsync(string payload, CancellationToken cancellationToken);
    ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The close frame observed by the most recent <see cref="ReceiveAsync"/> that returned
    /// <see langword="null"/>, or <see langword="null"/> if the socket has not closed.
    /// </summary>
    CopilotResponsesCloseFrame? LastClose { get; }
}

/// <summary>
/// Raised when the Copilot Responses WebSocket closes before a terminal response event, carrying the
/// close code so the SSE fallback can report a cause rather than just an exception type name (#3366).
/// </summary>
/// <remarks>
/// Derives from <see cref="IOException"/> because <see cref="WebSocketException"/> is sealed and cannot
/// be extended. The provider's fallback filter catches <see cref="Exception"/>, so the base type change
/// does not alter which failures fall back to SSE.
/// </remarks>
internal sealed class CopilotResponsesWebSocketClosedException(CopilotResponsesCloseFrame? close, string message)
    : IOException(message)
{
    public CopilotResponsesCloseFrame? Close { get; } = close;
}

internal sealed class CopilotResponsesWebSocketTransport : ICopilotResponsesWebSocketTransport
{
    private const int MaxMessageBytes = 16 * 1024 * 1024;
    private readonly ClientWebSocket _socket = new();

    /// <inheritdoc />
    public CopilotResponsesCloseFrame? LastClose { get; private set; }

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
            {
                // #3366: the close status is the only evidence the server sends explaining why it hung
                // up. Record it before returning null so the caller can attribute the fallback.
                // Note the Memory<byte> receive overload returns ValueWebSocketReceiveResult, which does
                // NOT carry the close status - it is surfaced on the socket itself once the close frame
                // has been observed, so read it from there rather than from the result struct.
                LastClose = new CopilotResponsesCloseFrame((int?)_socket.CloseStatus, _socket.CloseStatusDescription);
                return null;
            }
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

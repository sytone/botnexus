using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;

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

/// <summary>
/// Raised when the Copilot Responses WebSocket <em>upgrade handshake</em> is rejected with an HTTP
/// status rather than the expected <c>101</c>, carrying that status so the caller can decide whether
/// the failure is transport-degradation or terminal (#3674).
/// </summary>
/// <remarks>
/// Why a distinct type from <see cref="CopilotResponsesWebSocketClosedException"/>: a close frame
/// means the socket was established and later hung up, which is plausibly transient. A rejected
/// handshake means the socket was never established at all, and when the rejection is a 401/403 it is
/// a rejected <b>credential</b> - re-presenting that same credential over SSE cannot succeed. The
/// status has to survive as structured data, because the alternative (re-sniffing the CLR message at
/// each decision point) is exactly the string-matching the provider must not depend on.
/// <para>
/// Derives from <see cref="IOException"/> for the same reason as its sibling: <see cref="WebSocketException"/>
/// is sealed and cannot be extended.
/// </para>
/// </remarks>
internal sealed class CopilotResponsesWebSocketHandshakeException(int statusCode, string message, Exception? innerException)
    : IOException(message, innerException)
{
    /// <summary>The HTTP status the server returned instead of <c>101 Switching Protocols</c>.</summary>
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Extracts and classifies the HTTP status of a rejected WebSocket upgrade handshake.
/// </summary>
/// <remarks>
/// Two sources, in order of trustworthiness. <see cref="ClientWebSocket.HttpStatusCode"/> is the
/// structured one and is populated only when <c>CollectHttpResponseDetails</c> was set before the
/// connect, so the transport opts in. <see cref="TryParseStatus"/> is the fallback for the case where
/// the property is unavailable or unset: the CLR's own handshake failure message embeds the status,
/// and reading it is strictly better than discarding the evidence entirely. The parse is pinned by a
/// unit test so a runtime message change is a test failure rather than a silent regression to the
/// old "everything is transport" behaviour.
/// </remarks>
internal static partial class CopilotResponsesHandshakeStatus
{
    [GeneratedRegex(@"status code '(?<status>\d{3})' when status code '101' was expected", RegexOptions.CultureInvariant)]
    private static partial Regex StatusPattern();

    /// <summary>Reads the rejected-handshake status out of a CLR WebSocket failure message, if present.</summary>
    internal static int? TryParseStatus(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return null;
        var match = StatusPattern().Match(message);
        return match.Success && int.TryParse(match.Groups["status"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var status)
            ? status
            : null;
    }

    /// <summary>
    /// True when the status means the credential was rejected. 401 and 403 only: a 429 is a rate
    /// limit and a 5xx is a server fault, and both of those remain legitimately retryable over SSE.
    /// </summary>
    internal static bool IsAuthFailure(int statusCode) => statusCode is 401 or 403;
}

internal sealed class CopilotResponsesWebSocketTransport : ICopilotResponsesWebSocketTransport
{
    private const int MaxMessageBytes = 16 * 1024 * 1024;
    private readonly ClientWebSocket _socket = new()
    {
        // #3674: without this the handshake status is thrown away and a 403 is indistinguishable
        // from a dropped connection. The provider needs the status to refuse a pointless SSE retry.
        Options = { CollectHttpResponseDetails = true }
    };

    /// <inheritdoc />
    public CopilotResponsesCloseFrame? LastClose { get; private set; }

    public async ValueTask ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        foreach (var (key, value) in headers)
            _socket.Options.SetRequestHeader(key, value);
        try
        {
            await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            // Prefer the structured status collected by the client; fall back to the status the CLR
            // embedded in its own message. Only wrap when a status is actually known - a handshake
            // that failed with no HTTP response at all (DNS, TLS, reset) is a genuine transport fault
            // and must keep propagating as-is so the SSE fallback still covers it.
            var collected = (int)_socket.HttpStatusCode;
            var status = collected != 0 ? collected : CopilotResponsesHandshakeStatus.TryParseStatus(ex.Message);
            if (status is not int rejected)
                throw;
            throw new CopilotResponsesWebSocketHandshakeException(
                rejected,
                $"Copilot Responses WebSocket handshake was rejected with HTTP {rejected.ToString(System.Globalization.CultureInfo.InvariantCulture)}.",
                ex);
        }
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

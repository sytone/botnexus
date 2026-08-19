using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Extensions.Channels.Matrix;

/// <summary>
/// Raised when the homeserver returns a non-success status for a Client-Server API call.
/// </summary>
/// <remarks>
/// Carries the HTTP status so the polling loop's failure classifier can distinguish a transient
/// 429/5xx from a terminal 401/403 (a revoked or invalid access token), rather than retrying a
/// fault that cannot clear.
/// </remarks>
public sealed class MatrixApiException : Exception
{
    /// <summary>Initialises the exception.</summary>
    /// <param name="statusCode">HTTP status returned by the homeserver.</param>
    /// <param name="errorCode">Matrix <c>errcode</c> when present, e.g. <c>M_UNKNOWN_TOKEN</c>.</param>
    /// <param name="message">Human-readable description.</param>
    public MatrixApiException(HttpStatusCode statusCode, string? errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    /// <summary>HTTP status returned by the homeserver.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Matrix <c>errcode</c> when the homeserver supplied one.</summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Whether this fault is terminal - authentication or authorization failures that no amount of
    /// retrying can clear. Everything else (rate limits, 5xx, transport faults) is retryable.
    /// </summary>
    public bool IsTerminal =>
        StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

/// <summary>
/// <see cref="IMatrixClient"/> implementation over the Matrix Client-Server API v3 using a plain
/// <see cref="HttpClient"/>. No Matrix SDK dependency, per #1201's minimal-dependency preference.
/// </summary>
public sealed class MatrixHttpClient : IMatrixClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _userId;
    private readonly ISecretRedactor? _secretRedactor;

    // Matrix requires a client-generated transaction ID on send so a retried request is
    // de-duplicated by the homeserver rather than posting the message twice. A per-client
    // monotonic counter combined with the process-unique prefix is sufficient and avoids a
    // GUID allocation per send.
    private readonly string _txnPrefix = Guid.NewGuid().ToString("N");
    private long _txnCounter;

    /// <summary>
    /// Initialises the client for one account.
    /// </summary>
    /// <param name="http">
    /// HTTP client whose <see cref="HttpClient.BaseAddress"/> is the homeserver root. The
    /// per-request timeout must exceed the configured <c>/sync</c> long-poll timeout.
    /// </param>
    /// <param name="userId">The account's fully-qualified Matrix user ID.</param>
    /// <param name="accessToken">
    /// The account's access token. Unwrapped exactly once here, to build the <c>Authorization</c>
    /// header; the raw string is never stored on this instance.
    /// </param>
    /// <param name="secretRedactor">
    /// Optional redactor applied to homeserver-controlled error text before it is interpolated into a
    /// <see cref="MatrixApiException"/> message (#3398). The <c>Authorization</c> header set below
    /// carries the account access token, and a homeserver - or an error-echoing proxy in front of one -
    /// can reflect that header back in its error body or reason phrase, which would otherwise put the
    /// credential into gateway logs and agent-facing text. <see langword="null"/> is a deliberate
    /// no-op rather than a blanket drop, mirroring <c>ProviderHttpErrorHelper</c>, so an un-wired
    /// caller keeps its diagnostics instead of silently losing them.
    /// </param>
    public MatrixHttpClient(HttpClient http, string userId, MatrixAccessToken accessToken, ISecretRedactor? secretRedactor = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (!accessToken.HasValue)
            throw new ArgumentException("Access token has no value.", nameof(accessToken));

        _http = http;
        _userId = userId;
        _secretRedactor = secretRedactor;

        // Bearer header rather than the deprecated ?access_token= query parameter, so the secret
        // never lands in homeserver access logs or proxy traces. This is the single Reveal() site:
        // the token is handed to HttpClient and no copy is retained as a field.
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Reveal());
    }

    /// <inheritdoc />
    public async Task<MatrixSyncResponse> SyncAsync(string? since, int timeoutMs, CancellationToken cancellationToken)
    {
        var url = $"/_matrix/client/v3/sync?timeout={timeoutMs.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(since))
            url += $"&since={Uri.EscapeDataString(since)}";

        using var response = await _http.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, "sync", cancellationToken);

        return await response.Content.ReadFromJsonAsync<MatrixSyncResponse>(JsonOptions, cancellationToken)
               ?? new MatrixSyncResponse();
    }

    /// <inheritdoc />
    public async Task<string> SendMessageAsync(string roomId, MatrixMessageContent content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);
        ArgumentNullException.ThrowIfNull(content);

        var txnId = NextTransactionId();
        var url = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/send/m.room.message/{Uri.EscapeDataString(txnId)}";

        using var response = await _http.PutAsJsonAsync(url, content, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, "send message", cancellationToken);

        var body = await response.Content.ReadFromJsonAsync<MatrixSendResponse>(JsonOptions, cancellationToken);
        return body?.EventId ?? string.Empty;
    }

    /// <inheritdoc />
    public async Task JoinRoomAsync(string roomId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);

        var url = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/join";
        using var response = await _http.PostAsJsonAsync(url, new { }, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, "join room", cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetTypingAsync(string roomId, bool typing, int timeoutMs, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);

        var url = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/typing/{Uri.EscapeDataString(_userId)}";
        object payload = typing
            ? new { typing = true, timeout = timeoutMs }
            : new { typing = false };

        using var response = await _http.PutAsJsonAsync(url, payload, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, "set typing", cancellationToken);
    }

    private string NextTransactionId()
    {
        var ordinal = Interlocked.Increment(ref _txnCounter);
        return string.Concat(_txnPrefix, ".", ordinal.ToString(CultureInfo.InvariantCulture));
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        string? errorCode = null;
        string? errorText = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<MatrixErrorBody>(JsonOptions, cancellationToken);
            errorCode = error?.ErrorCode;
            errorText = error?.Error;
        }
        catch (Exception)
        {
            // A homeserver behind a proxy can return an HTML error page. The status code is the
            // load-bearing signal for classification, so an unparseable body must not mask it.
        }

        // Redact ONCE, before any interpolation, and cover BOTH reflection surfaces: a remote that
        // echoes a credential into its error body can just as easily put it in the reason phrase, so
        // scrubbing only the body would leave the second hole open (#3398). errcode is a fixed
        // Matrix enum-like token, but it is still remote-controlled text, so it goes through too.
        var safeErrorCode = Redact(errorCode);
        var safeDetail = Redact(errorText ?? response.ReasonPhrase);

        throw new MatrixApiException(
            response.StatusCode,
            errorCode,
            $"Matrix {operation} failed with HTTP {(int)response.StatusCode} ({safeErrorCode ?? "no errcode"}): {safeDetail}");
    }

    /// <summary>
    /// Applies the redactor to untrusted homeserver text. Null redactor and null/empty input are
    /// pass-through, so redaction can never turn a diagnosable failure into a blank one.
    /// </summary>
    private string? Redact(string? text)
        => _secretRedactor is null || string.IsNullOrEmpty(text) ? text : _secretRedactor.Redact(text);

    private sealed class MatrixErrorBody
    {
        [JsonPropertyName("errcode")]
        public string? ErrorCode { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}

/// <summary>
/// Default <see cref="IMatrixClientFactory"/> creating one <see cref="MatrixHttpClient"/> per
/// account from an <see cref="IHttpClientFactory"/>.
/// </summary>
/// <param name="httpClientFactory">Factory supplying pooled <see cref="HttpClient"/> instances.</param>
/// <param name="secretRedactor">
/// Optional redactor threaded into each client so homeserver-controlled error text is scrubbed
/// before it reaches a <see cref="MatrixApiException"/> message (#3398). Optional so the extension
/// still resolves in a host that has not registered one.
/// </param>
public sealed class DefaultMatrixClientFactory(IHttpClientFactory httpClientFactory, ISecretRedactor? secretRedactor = null) : IMatrixClientFactory
{
    /// <summary>
    /// Per-request HTTP timeout. Must exceed the maximum permitted <c>/sync</c> long-poll timeout
    /// (600s per <see cref="MatrixChannelOptions.SyncTimeoutMs"/>'s range) plus transport slack,
    /// otherwise a healthy long poll would be cancelled by the client as if the server had hung.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(660);

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ISecretRedactor? _secretRedactor = secretRedactor;

    /// <inheritdoc />
    public IMatrixClient Create(string accountName, string homeserver, string userId, MatrixAccessToken accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeserver);

        var http = _httpClientFactory.CreateClient($"matrix:{accountName}");
        http.BaseAddress = new Uri(homeserver.TrimEnd('/') + "/", UriKind.Absolute);
        http.Timeout = RequestTimeout;

        return new MatrixHttpClient(http, userId, accessToken, _secretRedactor);
    }
}

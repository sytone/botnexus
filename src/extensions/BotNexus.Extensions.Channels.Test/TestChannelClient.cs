using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// HTTP client wrapper an integration test uses to drive the test channel: inject inbound messages,
/// poll captured outbound deliveries, and read captured gateway logs.
/// </summary>
/// <remarks>
/// <para>
/// This lives in the EXTENSION project rather than in one test project on purpose. The client and
/// the endpoints it calls are one contract — route shape, request body, response shape — and
/// splitting them across two assemblies is how a helper silently drifts from the surface it wraps.
/// Any test project can consume it with a single project reference, and the endpoint tests in this
/// extension's mirror suite exercise both halves together.
/// </para>
/// <para>
/// The client owns no gateway state; every method is a plain HTTP call against a running gateway.
/// </para>
/// </remarks>
public sealed class TestChannelClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string _channelId;

    /// <summary>Creates a client against a gateway base URL.</summary>
    /// <param name="baseUrl">Gateway base URL, e.g. <c>http://localhost:5099</c>.</param>
    /// <param name="channelId">The channel key the test adapter is registered as.</param>
    public TestChannelClient(string baseUrl, string channelId)
        : this(new HttpClient { BaseAddress = new Uri(baseUrl, UriKind.Absolute) }, channelId, ownsClient: true)
    {
    }

    /// <summary>Creates a client over a caller-supplied <see cref="HttpClient"/>.</summary>
    /// <param name="http">
    /// Pre-configured client; its <see cref="HttpClient.BaseAddress"/> must point at the gateway.
    /// Use this overload with an in-memory test server.
    /// </param>
    /// <param name="channelId">The channel key the test adapter is registered as.</param>
    /// <param name="ownsClient">Whether disposing this client should dispose <paramref name="http"/>.</param>
    public TestChannelClient(HttpClient http, string channelId, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        _http = http;
        _channelId = channelId;
        _ownsClient = ownsClient;
    }

    /// <summary>The channel key this client addresses.</summary>
    public string ChannelId => _channelId;

    /// <summary>
    /// Injects an inbound message into the gateway as if it had arrived on this channel.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the gateway does not accept the injection — the channel is not loaded (404) or
    /// the adapter is not running (409). Throwing here rather than returning a status is
    /// deliberate: a test that ignored a failed injection would then time out waiting for a reply
    /// that was never going to arrive, and report the wrong cause.
    /// </exception>
    public async Task InjectMessageAsync(
        string content,
        string address,
        string? senderId = null,
        string? targetAgentId = null,
        string? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        var request = new TestChannelInboundRequest(address, content, senderId, targetAgentId, conversationId);
        using var response = await _http.PostAsJsonAsync(
            $"{TestChannelEndpointContributor.RoutePrefix}/{_channelId}/inbound",
            request,
            JsonOptions,
            cancellationToken);

        if (response.IsSuccessStatusCode)
            return;

        var reason = response.StatusCode switch
        {
            HttpStatusCode.NotFound =>
                $"the test channel '{_channelId}' is not loaded on this gateway (the extension ships disabled; enable it explicitly for this run)",
            HttpStatusCode.Conflict =>
                $"the test channel '{_channelId}' adapter is loaded but not running",
            _ => $"the gateway returned {(int)response.StatusCode} {response.StatusCode}",
        };

        throw new InvalidOperationException($"Inbound injection on '{address}' was not accepted: {reason}.");
    }

    /// <summary>Returns captured deliveries, optionally filtered to one address.</summary>
    /// <param name="address">Channel address to filter by; <c>null</c> returns every address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<TestChannelOutboundRecord>> GetOutboundAsync(
        string? address = null,
        CancellationToken cancellationToken = default)
    {
        var url = $"{TestChannelEndpointContributor.RoutePrefix}/{_channelId}/outbound";
        if (!string.IsNullOrWhiteSpace(address))
            url += $"?address={Uri.EscapeDataString(address)}";

        return await _http.GetFromJsonAsync<List<TestChannelOutboundRecord>>(url, JsonOptions, cancellationToken)
            ?? [];
    }

    /// <summary>Clears captured deliveries, optionally for one address only.</summary>
    /// <param name="address">Channel address to clear; <c>null</c> clears every address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ClearOutboundAsync(string? address = null, CancellationToken cancellationToken = default)
    {
        var url = $"{TestChannelEndpointContributor.RoutePrefix}/{_channelId}/outbound";
        if (!string.IsNullOrWhiteSpace(address))
            url += $"?address={Uri.EscapeDataString(address)}";

        using var response = await _http.DeleteAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Polls until a complete (non-delta) delivery matching <paramref name="predicate"/> appears, or
    /// the timeout elapses.
    /// </summary>
    /// <param name="address">Channel address to watch; <c>null</c> watches every address.</param>
    /// <param name="predicate">Optional additional match; defaults to "any complete message".</param>
    /// <param name="timeout">How long to wait. Defaults to five seconds.</param>
    /// <param name="pollInterval">Polling interval. Defaults to 100ms.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="TimeoutException">
    /// Thrown with the deliveries that WERE seen, so a failure reports what actually arrived rather
    /// than only that nothing matched.
    /// </exception>
    public async Task<TestChannelOutboundRecord> WaitForMessageAsync(
        string? address = null,
        Func<TestChannelOutboundRecord, bool>? predicate = null,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        IReadOnlyList<TestChannelOutboundRecord> seen = [];

        while (DateTimeOffset.UtcNow < deadline)
        {
            seen = await GetOutboundAsync(address, cancellationToken);

            // Stream deltas are excluded: a caller waiting for "the reply" that matched a partial
            // delta would assert against truncated text and read it as a content defect.
            var match = seen.FirstOrDefault(record =>
                !record.IsStreamDelta && (predicate is null || predicate(record)));

            if (match is not null)
                return match;

            await Task.Delay(interval, cancellationToken);
        }

        var observed = seen.Count == 0
            ? "no deliveries were captured at all"
            : "captured deliveries were: " + string.Join(
                " | ",
                seen.Select(record => $"[{record.Address}{(record.IsStreamDelta ? " delta" : string.Empty)}] {record.Content}"));

        throw new TimeoutException(
            $"No matching message arrived on test channel '{_channelId}'"
            + (address is null ? string.Empty : $" address '{address}'")
            + $" within {(timeout ?? TimeSpan.FromSeconds(5)).TotalSeconds}s; {observed}.");
    }

    /// <summary>Reads captured gateway log entries.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<TestChannelLogSnapshot> GetLogsAsync(CancellationToken cancellationToken = default)
        => await _http.GetFromJsonAsync<TestChannelLogSnapshot>(
            $"{TestChannelEndpointContributor.RoutePrefix}/logs",
            JsonOptions,
            cancellationToken)
            ?? new TestChannelLogSnapshot([], 0, 0);

    /// <summary>Clears the captured log buffer.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ClearLogsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(
            $"{TestChannelEndpointContributor.RoutePrefix}/logs",
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}

/// <summary>Response body of <c>GET /test-channel/logs</c>.</summary>
/// <param name="Entries">The retained log entries, in capture order.</param>
/// <param name="DroppedEntryCount">
/// Entries evicted because the ring buffer was full. Non-zero means <paramref name="Entries"/> is an
/// INCOMPLETE view, and an assertion of the form "this was never logged" cannot be supported from it.
/// </param>
/// <param name="Capacity">The buffer's retention bound.</param>
public sealed record TestChannelLogSnapshot(
    IReadOnlyList<TestChannelLogEntry> Entries,
    long DroppedEntryCount,
    int Capacity)
{
    /// <summary>Whether the snapshot is complete (nothing was evicted).</summary>
    public bool IsComplete => DroppedEntryCount == 0;
}

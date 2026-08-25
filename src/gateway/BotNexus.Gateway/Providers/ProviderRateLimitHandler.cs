using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Providers;

/// <summary>
/// Captures provider rate-limit headroom from every provider response.
/// </summary>
/// <remarks>
/// <para>
/// Sits in the shared provider <c>HttpClient</c> pipeline alongside the logging and retry handlers,
/// so one implementation covers every provider rather than each provider growing its own copy.
/// </para>
/// <para>
/// <b>It never reads the response body.</b> Agent turns stream as <c>text/event-stream</c> and the
/// streaming guard exists precisely to stop anything buffering them. Headers are available before
/// the body and cost nothing, which is the whole reason this is viable at all.
/// </para>
/// <para>
/// It does read the <em>request</em> body, to recover the model id. That is safe where the response
/// body is not: the request was serialised by this process moments earlier and is sitting in memory
/// as a buffered string, so reading it re-reads a local buffer rather than consuming a network
/// stream. It is size-capped anyway, because a large tool-result payload is not worth scanning to
/// find a field in the first 200 bytes.
/// </para>
/// <para>
/// Every failure path is swallowed. This is observability: a malformed header, an unparseable body
/// or an unknown host must never turn a working agent turn into an error.
/// </para>
/// </remarks>
public sealed class ProviderRateLimitHandler : DelegatingHandler
{
    /// <summary>Cap on the request bytes scanned for the model id.</summary>
    private const int MaxRequestScanBytes = 64 * 1024;

    private readonly IProviderUsageStore _store;
    private readonly ILogger<ProviderRateLimitHandler> _logger;

    /// <summary>Creates the handler.</summary>
    /// <param name="store">Where snapshots and samples are recorded.</param>
    /// <param name="logger">Logger; used only at Debug.</param>
    public ProviderRateLimitHandler(IProviderUsageStore store, ILogger<ProviderRateLimitHandler> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var model = await TryReadModelAsync(request, cancellationToken).ConfigureAwait(false);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        try
        {
            var provider = ResolveProvider(request.RequestUri);
            if (provider is not null)
            {
                var snapshot = Parse(provider, response, DateTimeOffset.UtcNow);
                var failed = !response.IsSuccessStatusCode;

                // A failed call is recorded even with no limit headers on the response. That is the
                // common shape for a 4xx - a 404 for a retired model id carries none at all - and
                // gating on HasAnyLimit made exactly those calls invisible. A burn panel that
                // silently ignores errors is worse than no panel: it reports calm while every send
                // is failing.
                if (snapshot.HasAnyLimit || failed)
                    _store.Record(snapshot, model, failed);
            }
        }
        catch (Exception ex)
        {
            // Observability must never break the call it is observing.
            _logger.LogDebug(ex, "Rate-limit capture failed; continuing.");
        }

        return response;
    }

    /// <summary>
    /// Maps a request host onto a canonical provider id.
    /// </summary>
    /// <param name="uri">The request URI.</param>
    /// <returns>The provider id, or null when the host is not a known model provider.</returns>
    /// <remarks>
    /// Host-based rather than configuration-based on purpose: the handler sits below the provider
    /// abstraction and has no provider identity passed to it, and the host is the one thing that is
    /// always true about where a call actually went.
    /// </remarks>
    public static string? ResolveProvider(Uri? uri)
    {
        var host = uri?.Host;
        if (string.IsNullOrWhiteSpace(host))
            return null;

        if (host.EndsWith("anthropic.com", StringComparison.OrdinalIgnoreCase)) return "anthropic";
        if (host.EndsWith("openai.com", StringComparison.OrdinalIgnoreCase)) return "openai";
        if (host.EndsWith("githubcopilot.com", StringComparison.OrdinalIgnoreCase)) return "github-copilot";
        if (host.EndsWith("models.github.ai", StringComparison.OrdinalIgnoreCase)) return "github-models";
        if (host.EndsWith("inference.ai.azure.com", StringComparison.OrdinalIgnoreCase)) return "github-models";
        return null;
    }

    /// <summary>
    /// Parses rate-limit headers into a snapshot, handling both reported dialects.
    /// </summary>
    /// <remarks>
    /// Anthropic prefixes <c>anthropic-ratelimit-</c> and states resets as RFC 3339 instants.
    /// OpenAI prefixes <c>x-ratelimit-</c> and states them as Go-style durations ("6m0s", "1s"),
    /// which is why the reset parser accepts both rather than assuming a timestamp.
    /// </remarks>
    /// <param name="provider">Canonical provider id.</param>
    /// <param name="response">The response whose headers to read.</param>
    /// <param name="nowUtc">Reference instant for duration-style resets.</param>
    /// <returns>A snapshot; every field null when the provider reported nothing.</returns>
    public static ProviderRateLimitSnapshot Parse(string provider, HttpResponseMessage response, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(response);

        long? Num(params string[] names)
        {
            foreach (var n in names)
            {
                if (response.Headers.TryGetValues(n, out var vals))
                {
                    var raw = vals.FirstOrDefault();
                    if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        return v;
                }
            }
            return null;
        }

        DateTimeOffset? Reset(params string[] names)
        {
            foreach (var n in names)
            {
                if (response.Headers.TryGetValues(n, out var vals))
                {
                    var raw = vals.FirstOrDefault();
                    var parsed = ParseReset(raw, nowUtc);
                    if (parsed is not null) return parsed;
                }
            }
            return null;
        }

        return new ProviderRateLimitSnapshot(
            Provider: provider,
            RequestsLimit: Num("anthropic-ratelimit-requests-limit", "x-ratelimit-limit-requests"),
            RequestsRemaining: Num("anthropic-ratelimit-requests-remaining", "x-ratelimit-remaining-requests"),
            RequestsResetUtc: Reset("anthropic-ratelimit-requests-reset", "x-ratelimit-reset-requests"),
            InputTokensLimit: Num("anthropic-ratelimit-input-tokens-limit"),
            InputTokensRemaining: Num("anthropic-ratelimit-input-tokens-remaining"),
            InputTokensResetUtc: Reset("anthropic-ratelimit-input-tokens-reset"),
            OutputTokensLimit: Num("anthropic-ratelimit-output-tokens-limit"),
            OutputTokensRemaining: Num("anthropic-ratelimit-output-tokens-remaining"),
            OutputTokensResetUtc: Reset("anthropic-ratelimit-output-tokens-reset"),
            TokensLimit: Num("anthropic-ratelimit-tokens-limit", "x-ratelimit-limit-tokens"),
            TokensRemaining: Num("anthropic-ratelimit-tokens-remaining", "x-ratelimit-remaining-tokens"),
            TokensResetUtc: Reset("anthropic-ratelimit-tokens-reset", "x-ratelimit-reset-tokens"),
            ObservedAtUtc: nowUtc);
    }

    /// <summary>
    /// Parses a reset value that may be an instant or a duration.
    /// </summary>
    /// <param name="raw">Raw header value.</param>
    /// <param name="nowUtc">Reference instant, for the duration form.</param>
    /// <returns>The absolute reset instant, or null.</returns>
    internal static DateTimeOffset? ParseReset(string? raw, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Trim();

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var instant))
            return instant;

        // Go-style duration: "1s", "6m0s", "1h2m3s", "150ms".
        var total = TimeSpan.Zero;
        var seen = false;
        var i = 0;
        while (i < raw.Length)
        {
            var start = i;
            while (i < raw.Length && (char.IsDigit(raw[i]) || raw[i] == '.')) i++;
            if (i == start) return null;
            if (!double.TryParse(raw[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return null;

            var unitStart = i;
            while (i < raw.Length && !char.IsDigit(raw[i]) && raw[i] != '.') i++;
            var unit = raw[unitStart..i];

            total += unit switch
            {
                "ms" => TimeSpan.FromMilliseconds(value),
                "s" => TimeSpan.FromSeconds(value),
                "m" => TimeSpan.FromMinutes(value),
                "h" => TimeSpan.FromHours(value),
                _ => TimeSpan.Zero,
            };
            if (unit is "ms" or "s" or "m" or "h") seen = true;
        }

        return seen ? nowUtc + total : null;
    }

    /// <summary>
    /// Recovers the <c>model</c> field from a buffered JSON request body.
    /// </summary>
    /// <param name="request">The outgoing request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The model id, or null when it cannot be determined cheaply.</returns>
    private static async Task<string?> TryReadModelAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.Content is null)
                return null;

            // Only inspect content already sitting in memory. Anything else could be a stream that
            // reading here would consume out from under the request itself.
            if (request.Content is not StringContent and not ByteArrayContent)
                return null;

            var length = request.Content.Headers.ContentLength;
            if (length is > MaxRequestScanBytes)
                return null;

            var json = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("model", out var m) &&
                m.ValueKind == JsonValueKind.String)
            {
                return m.GetString();
            }
        }
        catch
        {
            // A body that is not JSON, is too large, or cannot be re-read is simply not a source of
            // a model id. Never a reason to fail the request.
        }

        return null;
    }
}

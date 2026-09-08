namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Bounds the pre-authentication body read on the anonymous inbound webhook route
/// (<c>POST /api/webhooks/{agentId}/{webhookId}</c>) — issue #3807.
/// </summary>
/// <remarks>
/// <para>
/// The inbound endpoint must read the raw body <b>before</b> it can verify the HMAC signature,
/// because the signature is computed over those exact bytes. That ordering is correct and
/// unavoidable, but it turns the read into an <b>unauthenticated allocation primitive</b>: any
/// party who can reach the listener and knows a webhook path — and a webhook URL is by design
/// shared with external systems, so it is not secret — could otherwise force the gateway to
/// allocate an arbitrarily large managed array per request, with no bound on how many such reads
/// are in flight at once.
/// </para>
/// <para>
/// The rate limiter cannot compensate: it is opt-in, and even when enabled it is a
/// <i>request-count</i> window keyed by caller. It counts requests, never bytes, and never bounds
/// concurrent in-flight reads.
/// </para>
/// <para>
/// This guard therefore supplies the two bounds the route lacks — a byte ceiling and an in-flight
/// concurrency cap — and is deliberately scoped to the controller rather than applied as a global
/// Kestrel limit, so authenticated API routes that legitimately carry large payloads are unaffected.
/// </para>
/// </remarks>
public sealed class WebhookInboundBodyGuard : IDisposable
{
    /// <summary>Default byte ceiling for a pre-authentication webhook body read: 1 MiB.</summary>
    public const int DefaultMaxBodyBytes = 1024 * 1024;

    /// <summary>
    /// Default cap on concurrent pre-signature reads. Mirrors the upstream
    /// <c>maxInFlightPerKey</c> shape referenced by issue #3807.
    /// </summary>
    public const int DefaultMaxInFlightReads = 64;

    private readonly SemaphoreSlim _inFlight;

    /// <summary>Creates a guard with the supplied bounds.</summary>
    /// <param name="maxBodyBytes">Byte ceiling for the pre-auth read. Must be positive.</param>
    /// <param name="maxInFlightReads">Cap on concurrent pre-signature reads. Must be positive.</param>
    public WebhookInboundBodyGuard(
        int maxBodyBytes = DefaultMaxBodyBytes,
        int maxInFlightReads = DefaultMaxInFlightReads)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBodyBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInFlightReads);

        MaxBodyBytes = maxBodyBytes;
        MaxInFlightReads = maxInFlightReads;
        _inFlight = new SemaphoreSlim(maxInFlightReads, maxInFlightReads);
    }

    /// <summary>Byte ceiling applied to the pre-authentication body read.</summary>
    public int MaxBodyBytes { get; }

    /// <summary>Cap on concurrent pre-signature reads.</summary>
    public int MaxInFlightReads { get; }

    /// <summary>
    /// Rejects a request whose declared <c>Content-Length</c> already exceeds the ceiling, so a
    /// truthful oversized request costs nothing — no slot, no read, no allocation.
    /// </summary>
    /// <param name="contentLength">The declared content length, or <c>null</c> when absent.</param>
    /// <returns><c>true</c> when the declared length is already over the ceiling.</returns>
    public bool ExceedsDeclaredLength(long? contentLength) =>
        contentLength is { } declared && declared > MaxBodyBytes;

    /// <summary>
    /// Attempts to take one of the in-flight slots without waiting. A caller that fails to acquire
    /// must be rejected rather than queued: queueing an unauthenticated read is the same
    /// exhaustion primitive with a longer fuse.
    /// </summary>
    /// <returns><c>true</c> when a slot was taken and must later be released.</returns>
    public bool TryAcquireReadSlot() => _inFlight.Wait(0);

    /// <summary>Returns a previously acquired in-flight slot.</summary>
    public void ReleaseReadSlot() => _inFlight.Release();

    /// <summary>Slots currently available. Exposed for tests and diagnostics.</summary>
    public int AvailableReadSlots => _inFlight.CurrentCount;

    /// <summary>
    /// Copies at most <see cref="MaxBodyBytes"/> bytes from <paramref name="source"/>, stopping as
    /// soon as the ceiling is known to be exceeded.
    /// </summary>
    /// <remarks>
    /// The reader never buffers more than <see cref="MaxBodyBytes"/> plus one chunk, and it never
    /// makes the second full copy that <c>MemoryStream.ToArray()</c> would: an oversized body is
    /// abandoned in place rather than materialised and then measured.
    /// </remarks>
    /// <param name="source">The request body stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The read outcome — the body bytes, or an over-ceiling signal.</returns>
    public async Task<WebhookBodyReadResult> ReadBoundedAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        // One byte past the ceiling is all the evidence needed to reject, so the buffer is capped
        // there. A body of exactly MaxBodyBytes is accepted; MaxBodyBytes + 1 is not.
        var limit = MaxBodyBytes;
        var buffer = new byte[Math.Min(81920, limit + 1)];
        using var accumulated = new MemoryStream();

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            if (accumulated.Length + read > limit)
                return WebhookBodyReadResult.TooLarge(limit);

            accumulated.Write(buffer, 0, read);
        }

        return WebhookBodyReadResult.Ok(accumulated.ToArray());
    }

    /// <inheritdoc />
    public void Dispose() => _inFlight.Dispose();
}

/// <summary>Outcome of a bounded pre-authentication webhook body read.</summary>
/// <param name="Body">The body bytes when the read stayed within the ceiling; otherwise empty.</param>
/// <param name="IsTooLarge"><c>true</c> when the body exceeded the ceiling and was abandoned.</param>
/// <param name="Limit">The ceiling that was applied, for the rejection message.</param>
public readonly record struct WebhookBodyReadResult(byte[] Body, bool IsTooLarge, int Limit)
{
    /// <summary>A read that stayed within the ceiling.</summary>
    public static WebhookBodyReadResult Ok(byte[] body) => new(body, false, 0);

    /// <summary>A read abandoned because it exceeded <paramref name="limit"/> bytes.</summary>
    public static WebhookBodyReadResult TooLarge(int limit) => new([], true, limit);
}

using System.Text;
using System.Text.Json;

namespace BotNexus.Agent.Providers.Core.Utilities;

/// <summary>
/// Reads untrusted external HTTP response bodies with a hard byte cap so a hostile or
/// malfunctioning endpoint streaming an unbounded body cannot force the runtime to buffer the
/// whole payload before parsing (an availability / OOM-DoS vector).
/// </summary>
/// <remarks>
/// <para>
/// The framework default <see cref="HttpClient.MaxResponseContentBufferSize"/> is ~2 GB, which is
/// effectively unbounded for the JSON endpoints BotNexus talks to (web-search upstreams, model
/// discovery). <c>ReadAsStringAsync</c> / <c>ReadFromJsonAsync</c> buffer the <em>entire</em> body
/// with no limit. These helpers read the content <em>stream</em> incrementally and abort the moment
/// the cap is exceeded — without first materializing the full advertised body.
/// </para>
/// <para>
/// Two cheap rejections happen up front: a declared <c>Content-Length</c> larger than the cap is
/// rejected before a single body byte is pulled, and the streaming read itself stops as soon as the
/// running total crosses the cap (defending against a lying or chunked/no-length body).
/// </para>
/// <para>
/// Port of OpenClaw's <c>readProviderJsonResponse</c> / <c>readResponseWithLimit</c> campaign (16 MiB
/// shared cap), adapted to .NET <see cref="HttpContent"/>.
/// </para>
/// </remarks>
public static class BoundedHttpContent
{
    /// <summary>
    /// Default maximum response body size in bytes (16 MiB). Mirrors the OpenClaw shared cap. Far
    /// larger than any legitimate search / model-discovery JSON payload, yet small enough that a
    /// hostile endpoint cannot exhaust memory before the read is aborted.
    /// </summary>
    public const long DefaultMaxResponseBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Default maximum time allowed between two successive body chunks (30s). The total-byte cap
    /// bounds volume but not time: a provider (or middlebox) that opens a response and then trickles
    /// or wedges forever satisfies the byte cap indefinitely and holds the agent turn's slot open.
    /// This window is a <em>default</em> rather than opt-in precisely because an opt-in idle cap only
    /// protects the call sites that remember to pass it. Pass <see cref="Timeout.InfiniteTimeSpan"/>
    /// to deliberately opt out.
    /// </summary>
    public static readonly TimeSpan DefaultIdleChunkTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Mirrors the implicit options used by <c>System.Net.Http.Json.HttpContent.ReadFromJsonAsync</c>
    /// (case-insensitive property matching) so swapping callers onto the bounded reader does not
    /// change deserialization semantics.
    /// </summary>
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads the response content as a string, aborting if the body exceeds <paramref name="maxBytes"/>.
    /// </summary>
    /// <param name="content">The HTTP response content (untrusted external body).</param>
    /// <param name="maxBytes">Maximum number of bytes to read before aborting. Defaults to <see cref="DefaultMaxResponseBytes"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="idleTimeout">
    /// Maximum time allowed between two successive body chunks. Defaults to
    /// <see cref="DefaultIdleChunkTimeout"/>; pass <see cref="Timeout.InfiniteTimeSpan"/> to disable.
    /// </param>
    /// <returns>The decoded body as a string.</returns>
    /// <exception cref="ResponseContentTooLargeException">The body exceeds <paramref name="maxBytes"/>.</exception>
    /// <exception cref="ResponseBodyStalledException">No bytes arrived within <paramref name="idleTimeout"/>.</exception>
    public static async Task<string> ReadStringWithLimitAsync(
        HttpContent content,
        long maxBytes = DefaultMaxResponseBytes,
        CancellationToken cancellationToken = default,
        TimeSpan? idleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "maxBytes must be positive.");
        var idle = ResolveIdleTimeout(idleTimeout);

        // Cheap rejection: a declared Content-Length over the cap never needs a body byte pulled.
        var declaredLength = content.Headers.ContentLength;
        if (declaredLength is { } length && length > maxBytes)
            throw new ResponseContentTooLargeException(maxBytes, length);

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = await ReadBoundedAsync(stream, maxBytes, idle, cancellationToken).ConfigureAwait(false);

        // Honour a declared charset if present; default to UTF-8 (the JSON default).
        var charSet = content.Headers.ContentType?.CharSet;
        var encoding = ResolveEncoding(charSet);
        return encoding.GetString(buffer);
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> bytes of the response content and returns the decoded
    /// prefix together with a flag saying whether the body was longer than the cap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <b>truncating</b> sibling of <see cref="ReadStringWithLimitAsync"/> (#3399). The limiting
    /// reader treats an oversized body as a transport failure and discards it, which is right when the
    /// body is the payload being parsed. On an <em>error</em> path the body is only diagnostic context:
    /// throwing there would replace the real status-code diagnosis with a size complaint and lose the
    /// reason the request failed. So this variant keeps the bounded prefix instead of rejecting it,
    /// while still never buffering more than the cap.
    /// </para>
    /// <para>
    /// The cap is in <b>bytes</b>, applied to the undecoded stream, because that is the only unit that
    /// bounds memory before decoding. A declared <c>Content-Length</c> over the cap is NOT a rejection
    /// here - it merely predicts truncation - so a lying header cannot suppress the diagnostics.
    /// </para>
    /// </remarks>
    /// <param name="content">The HTTP response content (untrusted external body).</param>
    /// <param name="maxBytes">Maximum number of bytes to read. Must be positive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="idleTimeout">
    /// Maximum time allowed between two successive body chunks. Defaults to
    /// <see cref="DefaultIdleChunkTimeout"/>; pass <see cref="Timeout.InfiniteTimeSpan"/> to disable.
    /// </param>
    /// <returns>
    /// The decoded prefix and whether the body exceeded <paramref name="maxBytes"/>. The caller owns
    /// how truncation is surfaced (marker, log field); this method never mutates the text.
    /// </returns>
    public static async Task<(string Text, bool Truncated)> ReadStringPrefixAsync(
        HttpContent content,
        long maxBytes = DefaultMaxResponseBytes,
        CancellationToken cancellationToken = default,
        TimeSpan? idleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "maxBytes must be positive.");
        var idle = ResolveIdleTimeout(idleTimeout);

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        // Read one byte past the cap so "exactly at the cap" is distinguishable from "longer than the
        // cap"; the extra byte is then dropped from the returned prefix.
        var buffer = await ReadPrefixAsync(stream, maxBytes + 1, idle, cancellationToken).ConfigureAwait(false);
        var truncated = buffer.Length > maxBytes;
        var keep = truncated ? (int)maxBytes : buffer.Length;

        var encoding = ResolveEncoding(content.Headers.ContentType?.CharSet);
        return (encoding.GetString(buffer, 0, keep), truncated);
    }

    /// <summary>
    /// Reads the response content as JSON of type <typeparamref name="T"/>, aborting if the body
    /// exceeds <paramref name="maxBytes"/> before deserializing.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the JSON body into.</typeparam>
    /// <param name="content">The HTTP response content (untrusted external body).</param>
    /// <param name="options">Optional JSON serializer options.</param>
    /// <param name="maxBytes">Maximum number of bytes to read before aborting. Defaults to <see cref="DefaultMaxResponseBytes"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="idleTimeout">
    /// Maximum time allowed between two successive body chunks. Defaults to
    /// <see cref="DefaultIdleChunkTimeout"/>; pass <see cref="Timeout.InfiniteTimeSpan"/> to disable.
    /// </param>
    /// <returns>The deserialized value, or <c>null</c> when the body is empty / JSON null.</returns>
    /// <exception cref="ResponseContentTooLargeException">The body exceeds <paramref name="maxBytes"/>.</exception>
    /// <exception cref="ResponseBodyStalledException">No bytes arrived within <paramref name="idleTimeout"/>.</exception>
    public static async Task<T?> ReadFromJsonWithLimitAsync<T>(
        HttpContent content,
        JsonSerializerOptions? options = null,
        long maxBytes = DefaultMaxResponseBytes,
        CancellationToken cancellationToken = default,
        TimeSpan? idleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "maxBytes must be positive.");
        var idle = ResolveIdleTimeout(idleTimeout);

        var declaredLength = content.Headers.ContentLength;
        if (declaredLength is { } length && length > maxBytes)
            throw new ResponseContentTooLargeException(maxBytes, length);

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = await ReadBoundedAsync(stream, maxBytes, idle, cancellationToken).ConfigureAwait(false);

        if (buffer.Length == 0)
            return default;

        // Default to web defaults (case-insensitive property matching) to match the behaviour of
        // System.Net.Http.Json's ReadFromJsonAsync, which the callers previously used. Without this,
        // a lowercase JSON field (e.g. Ollama's "capabilities") would silently fail to bind.
        return JsonSerializer.Deserialize<T>(buffer, options ?? WebDefaults);
    }

    /// <summary>
    /// Reads <paramref name="stream"/> into a byte array, throwing once the running total would
    /// exceed <paramref name="maxBytes"/>. The read stops at the first chunk that crosses the cap,
    /// so an unbounded / lying body is never fully buffered.
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        long maxBytes,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        // 81920 == the default Stream.CopyToAsync buffer size.
        const int chunkSize = 81920;
        var rented = new byte[chunkSize];
        using var accumulator = new MemoryStream();
        long total = 0;
        var idleEnabled = idleTimeout != Timeout.InfiniteTimeSpan;

        while (true)
        {
            int read;

            // The deadline is PER CHUNK: a fresh linked source each iteration, so a slow-but-
            // progressing body never accumulates toward a whole-response budget. Mirrors the
            // linked-CTS idiom established by the bounded BeforeToolCall hook (#2547).
            using var idleCts = idleEnabled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            idleCts?.CancelAfter(idleTimeout);

            var readToken = idleCts?.Token ?? cancellationToken;
            try
            {
                read = await stream.ReadAsync(rented.AsMemory(0, chunkSize), readToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                idleCts is not null &&
                idleCts.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                // Idle-window breach, not caller cancellation. The caller's token is untouched, so
                // this is unambiguously the body going quiet rather than the turn being cancelled.
                throw new ResponseBodyStalledException(idleTimeout, total);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller cancellation must surface on the CALLER's token, not the linked one, so
                // upstream `when (ex.CancellationToken == myToken)` filters keep working.
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }

            if (read == 0)
                break;

            total += read;
            if (total > maxBytes)
                throw new ResponseContentTooLargeException(maxBytes, total);

            accumulator.Write(rented, 0, read);
        }

        return accumulator.ToArray();
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> bytes from <paramref name="stream"/>, stopping once the
    /// cap is reached rather than throwing. Shares the per-chunk idle-deadline discipline of
    /// <see cref="ReadBoundedAsync"/>; the only difference is what happens at the cap.
    /// </summary>
    private static async Task<byte[]> ReadPrefixAsync(
        Stream stream,
        long maxBytes,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        const int chunkSize = 81920;
        var rented = new byte[chunkSize];
        using var accumulator = new MemoryStream();
        long total = 0;
        var idleEnabled = idleTimeout != Timeout.InfiniteTimeSpan;

        while (total < maxBytes)
        {
            var want = (int)Math.Min(chunkSize, maxBytes - total);
            int read;

            using var idleCts = idleEnabled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            idleCts?.CancelAfter(idleTimeout);

            var readToken = idleCts?.Token ?? cancellationToken;
            try
            {
                read = await stream.ReadAsync(rented.AsMemory(0, want), readToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                idleCts is not null &&
                idleCts.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                throw new ResponseBodyStalledException(idleTimeout, total);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }

            if (read == 0)
                break;

            total += read;
            accumulator.Write(rented, 0, read);
        }

        return accumulator.ToArray();
    }

    /// <summary>
    /// Applies the non-null default and rejects a non-positive window, which would otherwise
    /// silently disable the guard - the exact failure mode this bound exists to prevent.
    /// </summary>
    private static TimeSpan ResolveIdleTimeout(TimeSpan? idleTimeout)
    {
        var idle = idleTimeout ?? DefaultIdleChunkTimeout;
        if (idle != Timeout.InfiniteTimeSpan && idle <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idleTimeout),
                idle,
                "idleTimeout must be positive, or Timeout.InfiniteTimeSpan to disable the idle deadline.");
        }

        return idle;
    }

    private static Encoding ResolveEncoding(string? charSet)
    {
        if (string.IsNullOrWhiteSpace(charSet))
            return Encoding.UTF8;

        try
        {
            // Strip surrounding quotes some servers emit, e.g. charset="utf-8".
            return Encoding.GetEncoding(charSet.Trim().Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}

/// <summary>
/// Thrown when an untrusted HTTP response body exceeds the configured byte cap. Treated as a
/// transport-level failure (the body is discarded; the read is aborted mid-flight).
/// </summary>
public sealed class ResponseContentTooLargeException : Exception
{
    /// <summary>The byte cap that was exceeded.</summary>
    public long MaxBytes { get; }

    /// <summary>
    /// The size that triggered the rejection: the declared <c>Content-Length</c> when rejected up
    /// front, otherwise the running byte count at the point the cap was crossed.
    /// </summary>
    public long ObservedBytes { get; }

    /// <summary>Initializes a new instance of the <see cref="ResponseContentTooLargeException"/> class.</summary>
    public ResponseContentTooLargeException(long maxBytes, long observedBytes)
        : base($"HTTP response body exceeded the {maxBytes}-byte limit (observed at least {observedBytes} bytes). The response was discarded to prevent excessive memory use.")
    {
        MaxBytes = maxBytes;
        ObservedBytes = observedBytes;
    }
}

/// <summary>
/// Thrown when an HTTP response body produced no further bytes within the per-chunk idle window.
/// Distinct from <see cref="ResponseContentTooLargeException"/> (volume) and from caller
/// cancellation (an <see cref="OperationCanceledException"/> carrying the caller's token), so a
/// stalled provider is diagnosable rather than indistinguishable from a slow but healthy one.
/// </summary>
public sealed class ResponseBodyStalledException : Exception
{
    /// <summary>The idle window that elapsed with no bytes received.</summary>
    public TimeSpan IdleTimeout { get; }

    /// <summary>
    /// Bytes successfully received before the stall. Zero distinguishes a body that never started
    /// from one that trickled and then wedged mid-flight.
    /// </summary>
    public long BytesReceived { get; }

    /// <summary>Initializes a new instance of the <see cref="ResponseBodyStalledException"/> class.</summary>
    public ResponseBodyStalledException(TimeSpan idleTimeout, long bytesReceived)
        : base($"HTTP response body stalled for {idleTimeout.TotalMilliseconds:0}ms with no further bytes (received {bytesReceived} bytes). The read was aborted to avoid holding the caller open indefinitely.")
    {
        IdleTimeout = idleTimeout;
        BytesReceived = bytesReceived;
    }
}

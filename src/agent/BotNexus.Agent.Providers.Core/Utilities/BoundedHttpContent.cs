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
    /// <returns>The decoded body as a string.</returns>
    /// <exception cref="ResponseContentTooLargeException">The body exceeds <paramref name="maxBytes"/>.</exception>
    public static async Task<string> ReadStringWithLimitAsync(
        HttpContent content,
        long maxBytes = DefaultMaxResponseBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "maxBytes must be positive.");

        // Cheap rejection: a declared Content-Length over the cap never needs a body byte pulled.
        var declaredLength = content.Headers.ContentLength;
        if (declaredLength is { } length && length > maxBytes)
            throw new ResponseContentTooLargeException(maxBytes, length);

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = await ReadBoundedAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);

        // Honour a declared charset if present; default to UTF-8 (the JSON default).
        var charSet = content.Headers.ContentType?.CharSet;
        var encoding = ResolveEncoding(charSet);
        return encoding.GetString(buffer);
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
    /// <returns>The deserialized value, or <c>null</c> when the body is empty / JSON null.</returns>
    /// <exception cref="ResponseContentTooLargeException">The body exceeds <paramref name="maxBytes"/>.</exception>
    public static async Task<T?> ReadFromJsonWithLimitAsync<T>(
        HttpContent content,
        JsonSerializerOptions? options = null,
        long maxBytes = DefaultMaxResponseBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "maxBytes must be positive.");

        var declaredLength = content.Headers.ContentLength;
        if (declaredLength is { } length && length > maxBytes)
            throw new ResponseContentTooLargeException(maxBytes, length);

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = await ReadBoundedAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);

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
        CancellationToken cancellationToken)
    {
        // 81920 == the default Stream.CopyToAsync buffer size.
        const int chunkSize = 81920;
        var rented = new byte[chunkSize];
        using var accumulator = new MemoryStream();
        long total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(rented.AsMemory(0, chunkSize), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            total += read;
            if (total > maxBytes)
                throw new ResponseContentTooLargeException(maxBytes, total);

            accumulator.Write(rented, 0, read);
        }

        return accumulator.ToArray();
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

using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Copilot.Streaming;

/// <summary>
/// A read-only <see cref="Stream"/> wrapper that bounds how much an untrusted SSE response body may
/// be read, defending the streaming Copilot transport against an unbounded-read OOM-DoS (issue
/// #1668). It is the streaming complement to <see cref="BoundedHttpContent"/> (issue #1653), which
/// bounded the non-streaming JSON / error bodies.
/// </summary>
/// <remarks>
/// <para>
/// The streaming parser pulls the SSE body line-by-line through a <see cref="StreamReader"/>. A
/// hostile or broken endpoint can stream a body that never ends, or a single <c>data:</c> line with
/// no newline, either of which would buffer without limit before the parser ever sees a boundary.
/// Because every byte the <see cref="StreamReader"/> consumes flows through this wrapper, the cap
/// trips regardless of how the reader buffers internally.
/// </para>
/// <para>
/// Two independent limits are enforced as bytes are read:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Total cap</b> - the aggregate number of bytes read across the whole response. Mirrors
/// <see cref="BoundedHttpContent.DefaultMaxResponseBytes"/> (16 MiB) so the streaming and
/// non-streaming paths agree on what a legitimate body size is.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Per-frame cap</b> - the number of bytes seen since the last newline (<c>\n</c>). An SSE frame
/// that cannot find its boundary within this many bytes is hostile/broken, so a single
/// never-terminating line is rejected long before it could approach the total cap.
/// </description>
/// </item>
/// </list>
/// <para>
/// On either overflow a <see cref="ResponseContentTooLargeException"/> is thrown - the same canonical
/// error <see cref="BoundedHttpContent"/> raises - which aborts the read mid-flight so the oversized
/// body is never fully materialized. Well-formed, under-cap streams pass through byte-identically;
/// the guard only trips on overflow and never alters normal parsing behaviour.
/// </para>
/// </remarks>
public sealed class ByteCountingStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly long _maxTotalBytes;
    private readonly long _maxFrameBytes;

    private long _totalBytesRead;
    private long _bytesSinceNewline;

    /// <summary>
    /// Initializes a new instance of the <see cref="ByteCountingStream"/> class.
    /// </summary>
    /// <param name="inner">The underlying response-body stream to read from. Must be readable.</param>
    /// <param name="maxTotalBytes">
    /// Hard cap on the total bytes read across the whole body. Should match
    /// <see cref="BoundedHttpContent.DefaultMaxResponseBytes"/> for the success path.
    /// </param>
    /// <param name="maxFrameBytes">
    /// Hard cap on bytes read since the last newline. Bounds a single unbounded SSE frame /
    /// <c>data:</c> line so it cannot grow without limit.
    /// </param>
    /// <param name="leaveOpen">
    /// When <c>true</c>, disposing this wrapper does not dispose <paramref name="inner"/>. Defaults
    /// to <c>true</c> because the caller typically owns the response-content stream lifetime.
    /// </param>
    public ByteCountingStream(Stream inner, long maxTotalBytes, long maxFrameBytes, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead)
            throw new ArgumentException("Inner stream must be readable.", nameof(inner));
        if (maxTotalBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTotalBytes), maxTotalBytes, "maxTotalBytes must be positive.");
        if (maxFrameBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFrameBytes), maxFrameBytes, "maxFrameBytes must be positive.");

        _inner = inner;
        _maxTotalBytes = maxTotalBytes;
        _maxFrameBytes = maxFrameBytes;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
            Account(buffer.AsSpan(offset, read));
        return read;
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        if (read > 0)
            Account(buffer[..read]);
        return read;
    }

    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        if (read > 0)
            Account(buffer.AsSpan(offset, read));
        return read;
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
            Account(buffer.Span[..read]);
        return read;
    }

    /// <inheritdoc />
    public override int ReadByte()
    {
        var b = _inner.ReadByte();
        if (b >= 0)
        {
            Span<byte> one = [(byte)b];
            Account(one);
        }
        return b;
    }

    // Accounts for a freshly-read span: bumps the running totals and trips the canonical overflow
    // error the moment either the aggregate or the current-frame cap is crossed. The frame counter
    // resets at every newline byte so normal multi-frame streams never trip it.
    private void Account(ReadOnlySpan<byte> chunk)
    {
        _totalBytesRead += chunk.Length;
        if (_totalBytesRead > _maxTotalBytes)
            throw new ResponseContentTooLargeException(_maxTotalBytes, _totalBytesRead);

        foreach (var b in chunk)
        {
            if (b == (byte)'\n')
            {
                _bytesSinceNewline = 0;
                continue;
            }

            _bytesSinceNewline++;
            if (_bytesSinceNewline > _maxFrameBytes)
                throw new ResponseContentTooLargeException(_maxFrameBytes, _bytesSinceNewline);
        }
    }

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => _totalBytesRead;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
        // No write path; nothing to flush. Forward for symmetry with the inner stream.
        _inner.Flush();
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
            await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

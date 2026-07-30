using System.Diagnostics;
using System.Text;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Core.Tests.Utilities;

/// <summary>
/// Covers the per-chunk idle deadline added for issue #2555: a provider that opens a response and
/// then trickles or stalls forever satisfies the total-byte cap indefinitely, so the read must also
/// be bounded by the time between chunks.
/// </summary>
public class BoundedHttpContentIdleTimeoutTests
{
    /// <summary>AC1: the idle window has a non-null default so every existing caller is protected.</summary>
    [Fact]
    public void DefaultIdleChunkTimeout_IsNonNullAndPositive()
    {
        BoundedHttpContent.DefaultIdleChunkTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
        BoundedHttpContent.DefaultIdleChunkTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    /// <summary>AC1/AC2: one chunk then a permanent stall fails within the idle window.</summary>
    [Fact]
    public async Task ReadStringWithLimitAsync_StreamStallsAfterFirstChunk_ThrowsStalledWithinWindow()
    {
        using var stream = new ChunkThenStallStream("hello");
        var content = new StreamContent(stream);

        var sw = Stopwatch.StartNew();
        var act = async () => await BoundedHttpContent.ReadStringWithLimitAsync(
            content,
            maxBytes: 1024,
            idleTimeout: TimeSpan.FromMilliseconds(200));

        var ex = await act.ShouldThrowAsync<ResponseBodyStalledException>();
        sw.Stop();

        ex.IdleTimeout.ShouldBe(TimeSpan.FromMilliseconds(200));
        ex.Message.ShouldContain("stalled");
        ex.Message.ShouldContain("200ms");
        // Hang guard: must fail promptly, not hold the read open.
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    /// <summary>AC2: the JSON path is bounded identically.</summary>
    [Fact]
    public async Task ReadFromJsonWithLimitAsync_StreamStalls_ThrowsStalled()
    {
        using var stream = new ChunkThenStallStream("{\"value\":");
        var content = new StreamContent(stream);

        var act = async () => await BoundedHttpContent.ReadFromJsonWithLimitAsync<SampleValue>(
            content,
            maxBytes: 1024,
            idleTimeout: TimeSpan.FromMilliseconds(200));

        var ex = await act.ShouldThrowAsync<ResponseBodyStalledException>();
        ex.IdleTimeout.ShouldBe(TimeSpan.FromMilliseconds(200));
    }

    /// <summary>
    /// AC3: caller cancellation surfaces as OperationCanceledException on the caller's token and is
    /// NOT misreported as a stall.
    /// </summary>
    [Fact]
    public async Task ReadStringWithLimitAsync_CallerCancels_ThrowsOperationCanceledNotStalled()
    {
        using var stream = new ChunkThenStallStream("hello");
        var content = new StreamContent(stream);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var act = async () => await BoundedHttpContent.ReadStringWithLimitAsync(
            content,
            maxBytes: 1024,
            // Idle window far longer than the caller cancellation, so the only cause is the caller.
            idleTimeout: TimeSpan.FromSeconds(30),
            cancellationToken: cts.Token);

        var ex = await act.ShouldThrowAsync<OperationCanceledException>();
        ex.ShouldNotBeOfType<ResponseBodyStalledException>();
        ex.CancellationToken.ShouldBe(cts.Token);
    }

    /// <summary>AC4: a slow-but-progressing stream completes regardless of total duration.</summary>
    [Fact]
    public async Task ReadStringWithLimitAsync_SlowButProgressingStream_Completes()
    {
        // Six 50ms gaps = 300ms total, each comfortably inside the 250ms idle window.
        using var stream = new TrickleStream("abcdef", TimeSpan.FromMilliseconds(50));
        var content = new StreamContent(stream);

        var body = await BoundedHttpContent.ReadStringWithLimitAsync(
            content,
            maxBytes: 1024,
            idleTimeout: TimeSpan.FromMilliseconds(250));

        body.ShouldBe("abcdef");
    }

    /// <summary>AC5: the total-byte cap behaviour is unchanged by the idle deadline.</summary>
    [Fact]
    public async Task ReadStringWithLimitAsync_OverCap_StillThrowsResponseContentTooLarge()
    {
        var content = new StringContent(new string('a', 4096), Encoding.UTF8, "text/plain");

        var act = async () => await BoundedHttpContent.ReadStringWithLimitAsync(
            content,
            maxBytes: 1024,
            idleTimeout: TimeSpan.FromSeconds(30));

        var ex = await act.ShouldThrowAsync<ResponseContentTooLargeException>();
        ex.MaxBytes.ShouldBe(1024);
    }

    /// <summary>AC6: the idle window is configurable - a longer window tolerates a longer gap.</summary>
    [Fact]
    public async Task ReadStringWithLimitAsync_ConfiguredWindow_TolerantOfGapShorterThanWindow()
    {
        using var stream = new TrickleStream("xy", TimeSpan.FromMilliseconds(300));
        var content = new StreamContent(stream);

        var body = await BoundedHttpContent.ReadStringWithLimitAsync(
            content,
            maxBytes: 1024,
            idleTimeout: TimeSpan.FromSeconds(5));

        body.ShouldBe("xy");
    }

    /// <summary>AC6: an infinite window disables the deadline (opt-out remains possible).</summary>
    [Fact]
    public async Task ReadStringWithLimitAsync_InfiniteIdleTimeout_DisablesDeadline()
    {
        using var stream = new TrickleStream("xy", TimeSpan.FromMilliseconds(150));
        var content = new StreamContent(stream);

        var body = await BoundedHttpContent.ReadStringWithLimitAsync(
            content,
            maxBytes: 1024,
            idleTimeout: Timeout.InfiniteTimeSpan);

        body.ShouldBe("xy");
    }

    /// <summary>A non-positive idle window is a caller bug, not a silently disabled guard.</summary>
    [Fact]
    public async Task ReadStringWithLimitAsync_ZeroIdleTimeout_Throws()
    {
        var content = new StringContent("x");

        var act = async () => await BoundedHttpContent.ReadStringWithLimitAsync(
            content,
            maxBytes: 1024,
            idleTimeout: TimeSpan.Zero);

        await act.ShouldThrowAsync<ArgumentOutOfRangeException>();
    }

    private sealed class SampleValue
    {
        public int Value { get; set; }
    }

    /// <summary>
    /// Emits one chunk and then never completes another read (honouring cancellation only). Stands
    /// in for a provider that opens a response and then wedges mid-body.
    /// </summary>
    private sealed class ChunkThenStallStream : Stream
    {
        private readonly byte[] _first;
        private bool _emitted;

        public ChunkThenStallStream(string first) => _first = Encoding.UTF8.GetBytes(first);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_emitted)
            {
                _emitted = true;
                _first.CopyTo(buffer);
                return _first.Length;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Emits one byte per read after a fixed delay, so total duration is long but every inter-chunk
    /// gap is short. Distinguishes "slow" from "stalled".
    /// </summary>
    private sealed class TrickleStream : Stream
    {
        private readonly byte[] _payload;
        private readonly TimeSpan _gap;
        private int _index;

        public TrickleStream(string payload, TimeSpan gap)
        {
            _payload = Encoding.UTF8.GetBytes(payload);
            _gap = gap;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_gap, cancellationToken).ConfigureAwait(false);
            if (_index >= _payload.Length)
                return 0;

            buffer.Span[0] = _payload[_index++];
            return 1;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

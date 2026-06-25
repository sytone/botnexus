using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Core.Tests.Utilities;

public class BoundedHttpContentTests
{
    [Fact]
    public async Task ReadStringWithLimitAsync_UnderCap_ReturnsBody()
    {
        var content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json");

        var body = await BoundedHttpContent.ReadStringWithLimitAsync(content, maxBytes: 1024);

        body.ShouldBe("""{"ok":true}""");
    }

    [Fact]
    public async Task ReadStringWithLimitAsync_OverCap_ThrowsBeforeBufferingWholeBody()
    {
        // A body far larger than the cap. The reader must abort once the running total crosses the
        // cap, NOT pull the whole advertised body first.
        var content = new StringContent(new string('a', 4096), Encoding.UTF8, "text/plain");

        var act = async () => await BoundedHttpContent.ReadStringWithLimitAsync(content, maxBytes: 1024);

        var ex = await act.ShouldThrowAsync<ResponseContentTooLargeException>();
        ex.MaxBytes.ShouldBe(1024);
    }

    [Fact]
    public async Task ReadStringWithLimitAsync_DeclaredContentLengthOverCap_RejectsWithoutReadingBody()
    {
        // The stream would never end if read; the declared Content-Length must reject up front so
        // not a single body byte is pulled.
        using var stream = new NeverEndingStream();
        var content = new StreamContent(stream);
        content.Headers.ContentLength = long.MaxValue;

        var act = async () => await BoundedHttpContent.ReadStringWithLimitAsync(content, maxBytes: 1024);

        var ex = await act.ShouldThrowAsync<ResponseContentTooLargeException>();
        ex.ObservedBytes.ShouldBe(long.MaxValue);
        // If a body byte had been pulled the test would hang; reaching here proves early rejection.
        stream.BytesRead.ShouldBe(0);
    }

    [Fact]
    public async Task ReadStringWithLimitAsync_UnboundedNoLengthStream_AbortsMidFlight()
    {
        // No Content-Length (chunked / lying endpoint). The streaming read itself must abort once
        // it has read past the cap — proving we never buffer the whole infinite body.
        using var stream = new NeverEndingStream();
        var content = new StreamContent(stream);

        var act = async () => await BoundedHttpContent.ReadStringWithLimitAsync(content, maxBytes: 1024);

        await act.ShouldThrowAsync<ResponseContentTooLargeException>();
        // Bounded by one chunk past the cap, never the full (infinite) body.
        stream.BytesRead.ShouldBeLessThan(10L * 1024 * 1024);
        stream.BytesRead.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ReadStringWithLimitAsync_HonoursDeclaredCharset()
    {
        // Latin-1 'é' (0xE9) — proves we decode with the declared charset, not blindly UTF-8.
        var bytes = new byte[] { 0xE9 };
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "iso-8859-1" };

        var body = await BoundedHttpContent.ReadStringWithLimitAsync(content, maxBytes: 1024);

        body.ShouldBe("é");
    }

    [Fact]
    public async Task ReadFromJsonWithLimitAsync_UnderCap_Deserializes()
    {
        var content = new StringContent("""{"value":42}""", Encoding.UTF8, "application/json");

        var result = await BoundedHttpContent.ReadFromJsonWithLimitAsync<Sample>(content, maxBytes: 1024);

        result.ShouldNotBeNull();
        result!.Value.ShouldBe(42);
    }

    [Fact]
    public async Task ReadFromJsonWithLimitAsync_OverCap_ThrowsResponseContentTooLarge()
    {
        var bigJson = "{\"value\":42,\"pad\":\"" + new string('x', 4096) + "\"}";
        var content = new StringContent(bigJson, Encoding.UTF8, "application/json");

        var act = async () => await BoundedHttpContent.ReadFromJsonWithLimitAsync<Sample>(content, maxBytes: 1024);

        await act.ShouldThrowAsync<ResponseContentTooLargeException>();
    }

    [Fact]
    public async Task ReadFromJsonWithLimitAsync_EmptyBody_ReturnsDefault()
    {
        var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

        var result = await BoundedHttpContent.ReadFromJsonWithLimitAsync<Sample>(content, maxBytes: 1024);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ReadStringWithLimitAsync_NonPositiveCap_Throws()
    {
        var content = new StringContent("x");

        var act = async () => await BoundedHttpContent.ReadStringWithLimitAsync(content, maxBytes: 0);

        await act.ShouldThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DefaultMaxResponseBytes_Is16MiB()
    {
        BoundedHttpContent.DefaultMaxResponseBytes.ShouldBe(16L * 1024 * 1024);
    }

    private sealed class Sample
    {
        public int Value { get; set; }
    }

    /// <summary>
    /// A read stream that returns bytes forever — stands in for a hostile endpoint streaming an
    /// unbounded body. Records how many bytes were actually pulled so a test can prove the bounded
    /// reader aborted instead of draining it.
    /// </summary>
    private sealed class NeverEndingStream : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'a', offset, count);
            BytesRead += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill((byte)'a');
            BytesRead += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

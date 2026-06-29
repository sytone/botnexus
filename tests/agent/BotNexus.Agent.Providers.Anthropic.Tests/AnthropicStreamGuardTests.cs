using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

/// <summary>
/// Guards the Anthropic Messages streaming transport against unbounded body reads (OOM-DoS,
/// issue #1685). Mirrors the merged Copilot guard (#1668): the untrusted SSE success stream is
/// wrapped in <see cref="ByteCountingStream"/> (16 MiB total + 8 MiB per-frame) and the non-2xx
/// error body is read via <see cref="BoundedHttpContent.ReadStringWithLimitAsync"/>. A hostile or
/// MITM'd Anthropic-compatible endpoint can stream a never-ending success body, a single
/// never-terminating <c>data:</c> line, or a multi-GB error body; all three must abort mid-flight
/// rather than buffer without limit. Well-formed under-cap streams must parse unchanged.
/// </summary>
public class AnthropicStreamGuardTests
{
    private const long TotalCap = BoundedHttpContent.DefaultMaxResponseBytes; // 16 MiB

    [Fact]
    public async Task Stream_OverCapTotalSuccessBody_SurfacesOverflowError()
    {
        // 200 OK SSE body whose aggregate size crosses the 16 MiB total cap. The wrapped stream must
        // abort with the overflow error instead of buffering the whole body.
        var line = ": " + new string('p', 1021) + "\n"; // 1024-byte SSE comment frame
        var chunk = Encoding.ASCII.GetBytes(line);
        var body = new EndlessLineStream(chunk, repeatChunk: false, totalLength: TotalCap + (1L * 1024 * 1024));

        var result = await RunProviderAsync(HttpStatusCode.OK, body);

        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("limit", Case.Insensitive);
    }

    [Fact]
    public async Task Stream_SingleNeverTerminatingDataLine_SurfacesOverflowError()
    {
        // One enormous "data:" line with NO newline -> the 8 MiB per-frame cap must trip before the
        // line can grow without bound, while the total stays under the 16 MiB total cap.
        var prefix = Encoding.UTF8.GetBytes("data: {\"type\":\"content_block_delta\",\"junk\":\"");
        var junk = Encoding.ASCII.GetBytes(new string('Z', 10 * 1024 * 1024)); // 10 MiB, no '\n'
        var body = new byte[prefix.Length + junk.Length];
        Buffer.BlockCopy(prefix, 0, body, 0, prefix.Length);
        Buffer.BlockCopy(junk, 0, body, prefix.Length, junk.Length);

        var result = await RunProviderAsync(HttpStatusCode.OK, new MemoryStream(body));

        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("limit", Case.Insensitive);
    }

    [Fact]
    public async Task Stream_OverCapErrorBody_DoesNotBufferUnbounded()
    {
        // A non-2xx whose error body exceeds the cap. The bounded read must abort rather than buffer
        // the whole multi-MiB string; the failed-response path still surfaces an error to the stream.
        var huge = Encoding.ASCII.GetBytes(new string('e', 20 * 1024 * 1024)); // 20 MiB error body

        var result = await RunProviderAsync(HttpStatusCode.InternalServerError, new MemoryStream(huge));

        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldNotBeNull();
    }

    [Fact]
    public async Task Stream_NormalWellFormedStream_ParsesUnaffected()
    {
        var body =
            "event: message_start\n" +
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_anthropic_guard\",\"usage\":{\"input_tokens\":7,\"output_tokens\":0}}}\n" +
            "\n" +
            "event: content_block_start\n" +
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\"}}\n" +
            "\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hello\"}}\n" +
            "\n" +
            "event: content_block_stop\n" +
            "data: {\"type\":\"content_block_stop\",\"index\":0}\n" +
            "\n" +
            "event: message_delta\n" +
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":3}}\n" +
            "\n" +
            "event: message_stop\n" +
            "data: {\"type\":\"message_stop\"}\n";

        var result = await RunProviderAsync(HttpStatusCode.OK, new MemoryStream(Encoding.UTF8.GetBytes(body)));

        result.StopReason.ShouldNotBe(StopReason.Error);
        result.ErrorMessage.ShouldBeNull();
        (result.Usage.Input + result.Usage.Output).ShouldBeGreaterThan(0);
        result.Content.ShouldNotBeEmpty();
    }

    private static async Task<AssistantMessage> RunProviderAsync(HttpStatusCode status, Stream body)
    {
        var handler = new StreamingHandler(status, body);
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel(id: "claude-guard-test");
        var context = TestHelpers.MakeContext("guard");
        var stream = provider.Stream(model, context, new SimpleStreamOptions { ApiKey = "test-key" });
        return await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class StreamingHandler(HttpStatusCode status, Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                // StreamContent with no Content-Length forces the chunked streaming read path, so the
                // body cannot be pre-rejected on a declared length and must be bounded mid-flight.
                Content = new StreamContent(body),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// A test stream that emits a chunk, optionally repeating it (or up to a total length), without
    /// ever inserting an extra newline. Models a hostile endpoint that never stops sending.
    /// </summary>
    private sealed class EndlessLineStream(byte[] chunk, bool repeatChunk, long totalLength = long.MaxValue) : Stream
    {
        private readonly byte[] _chunk = chunk;
        private readonly bool _repeatChunk = repeatChunk;
        private readonly long _totalLength = totalLength;
        private long _produced;
        private int _offset;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_produced >= _totalLength)
                return 0;
            if (_offset >= _chunk.Length)
            {
                if (!_repeatChunk && _totalLength == long.MaxValue)
                    return 0;
                _offset = 0;
            }
            var available = _chunk.Length - _offset;
            var remainingTotal = _totalLength - _produced;
            var cap = remainingTotal > int.MaxValue ? int.MaxValue : (int)remainingTotal;
            var toCopy = Math.Min(Math.Min(available, count), cap);
            if (toCopy <= 0)
                return 0;
            Buffer.BlockCopy(_chunk, _offset, buffer, offset, toCopy);
            _offset += toCopy;
            _produced += toCopy;
            return toCopy;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var temp = new byte[buffer.Length];
            var n = Read(temp, 0, temp.Length);
            temp.AsSpan(0, n).CopyTo(buffer.Span);
            return ValueTask.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _produced; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Copilot.Tests.Messages;

/// <summary>
/// Guards the streaming SSE parser against unbounded body reads (OOM-DoS, issue #1668).
/// The streaming complement to the non-streaming bound shipped in #1653
/// (<see cref="BoundedHttpContent"/>). A hostile or broken Copilot endpoint can stream an
/// unbounded SSE body, or a single never-terminating <c>data:</c> line with no newline, which
/// would otherwise buffer without limit. These tests assert the byte guard trips on both
/// overflow shapes while leaving well-formed under-cap streams byte-identical.
/// </summary>
public class CopilotMessagesStreamGuardTests
{
    // The total success-body cap the parser enforces (16 MiB). Must stay aligned with
    // BoundedHttpContent.DefaultMaxResponseBytes so the streaming and non-streaming paths agree.
    private const long TotalCap = BoundedHttpContent.DefaultMaxResponseBytes;

    // A single SSE frame that cannot find its boundary within this many bytes is hostile/broken.
    private const long FrameCap = 64L * 1024;

    // ----------------------------------------------------------------------------------------
    // Direct unit tests for the reusable byte guard wrapper.
    // ----------------------------------------------------------------------------------------

    [Fact]
    public async Task ByteCountingStream_PassesThroughUnderCapBytesUnchanged()
    {
        var payload = Encoding.UTF8.GetBytes("event: message_stop\ndata: {\"type\":\"message_stop\"}\n");
        await using var inner = new MemoryStream(payload);
        await using var guarded = new ByteCountingStream(inner, maxTotalBytes: TotalCap, maxFrameBytes: FrameCap);

        using var copy = new MemoryStream();
        await guarded.CopyToAsync(copy);

        copy.ToArray().ShouldBe(payload);
    }

    [Fact]
    public async Task ByteCountingStream_ThrowsWhenTotalExceedsCap()
    {
        // A body that is well-formed line-by-line (so the per-frame cap never trips) but whose
        // aggregate size crosses the total cap. Use a tiny total cap to keep the test fast.
        const long smallTotalCap = 4096;
        var bytes = Encoding.ASCII.GetBytes(new string('a', 64) + "\n");
        await using var inner = new EndlessLineStream(bytes, repeatChunk: true);
        await using var guarded = new ByteCountingStream(inner, maxTotalBytes: smallTotalCap, maxFrameBytes: FrameCap);

        var buffer = new byte[1024];
        var ex = await Should.ThrowAsync<ResponseContentTooLargeException>(async () =>
        {
            // Drain until the running total crosses the cap.
            while (await guarded.ReadAsync(buffer) > 0)
            {
            }
        });

        ex.MaxBytes.ShouldBe(smallTotalCap);
        ex.ObservedBytes.ShouldBeGreaterThan(smallTotalCap);
    }

    [Fact]
    public async Task ByteCountingStream_ThrowsWhenSingleFrameExceedsCap()
    {
        // A single never-terminating "line": bytes with no newline at all. The per-frame cap must
        // trip well before the (much larger) total cap, so a hostile endless data: line cannot grow.
        const long smallFrameCap = 4096;
        var noNewlineChunk = Encoding.ASCII.GetBytes(new string('x', 1024)); // never contains '\n'
        await using var inner = new EndlessLineStream(noNewlineChunk, repeatChunk: true);
        await using var guarded = new ByteCountingStream(inner, maxTotalBytes: TotalCap, maxFrameBytes: smallFrameCap);

        var buffer = new byte[512];
        var ex = await Should.ThrowAsync<ResponseContentTooLargeException>(async () =>
        {
            while (await guarded.ReadAsync(buffer) > 0)
            {
            }
        });

        ex.MaxBytes.ShouldBe(smallFrameCap);
        ex.ObservedBytes.ShouldBeGreaterThan(smallFrameCap);
    }

    [Fact]
    public async Task ByteCountingStream_ResetsFrameCounterOnNewline()
    {
        // Many small frames separated by newlines must never trip the per-frame cap even though
        // their aggregate dwarfs it. This proves the frame counter resets at each '\n'.
        const long smallFrameCap = 256;
        var frame = Encoding.ASCII.GetBytes(new string('y', 200) + "\n"); // 201 bytes, under the 256 cap
        var body = new byte[frame.Length * 100];
        for (var i = 0; i < 100; i++)
            Buffer.BlockCopy(frame, 0, body, i * frame.Length, frame.Length);

        await using var inner = new MemoryStream(body);
        await using var guarded = new ByteCountingStream(inner, maxTotalBytes: TotalCap, maxFrameBytes: smallFrameCap);

        using var copy = new MemoryStream();
        await guarded.CopyToAsync(copy); // must NOT throw

        copy.ToArray().ShouldBe(body);
    }

    // ----------------------------------------------------------------------------------------
    // End-to-end tests through the real provider + parser (ProcessStreamAsync L43 read loop).
    // ----------------------------------------------------------------------------------------

    [Fact]
    public async Task ProcessStreamAsync_OverCapTotalBody_SurfacesOverflowError()
    {
        // A success (200) SSE response whose total body exceeds the 16 MiB cap. The parser must
        // abort with the overflow error rather than buffering the whole body.
        var oversized = BuildOversizedWellFormedSse(TotalCap + (1L * 1024 * 1024));
        var result = await RunProviderAsync(new EndlessLineStream(oversized.Chunk, repeatChunk: false, totalLength: oversized.TotalLength));

        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("limit", Case.Insensitive);
    }

    [Fact]
    public async Task ProcessStreamAsync_SingleNeverTerminatingDataLine_SurfacesOverflowError()
    {
        // One enormous "data:" line with NO trailing newline -> the inter-event / per-frame cap
        // must trip so the line cannot grow without bound. The total stays under 16 MiB so this
        // specifically exercises the frame cap, not the total cap.
        var prefix = Encoding.UTF8.GetBytes("data: {\"type\":\"content_block_delta\",\"junk\":\"");
        // ~512 KiB of junk with no '\n' anywhere -> exceeds the 64 KiB frame cap.
        var junk = Encoding.ASCII.GetBytes(new string('Z', 512 * 1024));
        var body = new byte[prefix.Length + junk.Length];
        Buffer.BlockCopy(prefix, 0, body, 0, prefix.Length);
        Buffer.BlockCopy(junk, 0, body, prefix.Length, junk.Length);

        var result = await RunProviderAsync(new MemoryStream(body));

        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("limit", Case.Insensitive);
    }

    [Fact]
    public async Task ProcessStreamAsync_NormalWellFormedStream_ParsesUnaffected()
    {
        // Regression guard: a normal, small, well-formed SSE stream must parse exactly as before;
        // the guard only trips on overflow and must never alter under-cap behaviour.
        var body =
            "event: message_start\n" +
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_guard_regression\",\"usage\":{\"input_tokens\":7,\"output_tokens\":0}}}\n" +
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

        var result = await RunProviderAsync(new MemoryStream(Encoding.UTF8.GetBytes(body)));

        result.StopReason.ShouldNotBe(StopReason.Error);
        result.ErrorMessage.ShouldBeNull();
        (result.Usage.Input + result.Usage.Output).ShouldBeGreaterThan(0);
        result.Content.ShouldNotBeEmpty();
    }

    // ----------------------------------------------------------------------------------------
    // Helpers.
    // ----------------------------------------------------------------------------------------

    private static async Task<AssistantMessage> RunProviderAsync(Stream responseBody)
    {
        var handler = new StreamingHandler(responseBody);
        var provider = new CopilotMessagesProvider(new HttpClient(handler));

        var model = new LlmModel(
            Id: "claude-guard-test",
            Name: "claude-guard-test",
            Api: CopilotMessagesProvider.ApiId,
            Provider: "github-copilot",
            BaseUrl: "https://api.enterprise.githubcopilot.com",
            Reasoning: true,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 200000,
            MaxTokens: 16384);

        var context = new Context(
            SystemPrompt: "guard",
            Messages: [new UserMessage(new UserMessageContent("guard"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        return await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    // Builds a small repeating well-formed SSE chunk plus the total length needed to exceed a cap.
    // The chunk is a complete SSE comment line ending in '\n' so the per-frame cap is never the
    // tripwire -- only the aggregate total can cross the limit.
    private static (byte[] Chunk, long TotalLength) BuildOversizedWellFormedSse(long minTotalBytes)
    {
        // A 1 KiB comment line per frame (SSE comment lines start with ':') keeps frames small.
        var line = ": " + new string('p', 1021) + "\n"; // 1024 bytes
        var chunk = Encoding.ASCII.GetBytes(line);
        return (chunk, minTotalBytes);
    }

    private sealed class StreamingHandler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                // StreamContent with no Content-Length forces the streaming read path (chunked),
                // so the body cannot be pre-rejected on a declared length and must be bounded
                // mid-flight by the parser's byte guard.
                Content = new StreamContent(body),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// A test stream that emits a chunk, optionally repeating it (or up to a total length),
    /// without ever inserting an extra newline. Models a hostile endpoint that never stops sending.
    /// </summary>
    private sealed class EndlessLineStream : Stream
    {
        private readonly byte[] _chunk;
        private readonly bool _repeatChunk;
        private readonly long _totalLength;
        private long _produced;
        private int _offset;

        public EndlessLineStream(byte[] chunk, bool repeatChunk, long totalLength = long.MaxValue)
        {
            _chunk = chunk;
            _repeatChunk = repeatChunk;
            _totalLength = totalLength;
        }

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

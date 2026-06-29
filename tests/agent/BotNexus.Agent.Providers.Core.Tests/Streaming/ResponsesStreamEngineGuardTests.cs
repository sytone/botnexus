using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.Core.Utilities;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// Guards the shared Responses engine (the OpenAI / Copilot Responses hot path) against unbounded
/// body reads (OOM-DoS, issue #1685). Mirrors the merged Copilot guard (#1668): the untrusted SSE
/// success stream is wrapped in <see cref="ByteCountingStream"/> (16 MiB total + 8 MiB per-frame)
/// and the non-2xx error body is read via <see cref="BoundedHttpContent.ReadStringWithLimitAsync"/>.
/// A minimal transport profile drains the reader line-by-line so the engine's read loop is what
/// bounds the body.
/// </summary>
public class ResponsesStreamEngineGuardTests
{
    private const long TotalCap = BoundedHttpContent.DefaultMaxResponseBytes; // 16 MiB

    private static LlmModel Model() => new(
        Id: "gpt-guard",
        Name: "gpt-guard",
        Api: "openai-responses",
        Provider: "openai",
        BaseUrl: "https://api.openai.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 8192,
        MaxTokens: 2048);

    [Fact]
    public async Task Stream_OverCapTotalSuccessBody_SurfacesOverflowError()
    {
        var line = ": " + new string('p', 1021) + "\n";
        var chunk = Encoding.ASCII.GetBytes(line);
        var body = new EndlessLineStream(chunk, repeatChunk: false, totalLength: TotalCap + (1L * 1024 * 1024));

        var result = await RunAsync(HttpStatusCode.OK, body);

        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("limit", Case.Insensitive);
    }

    [Fact]
    public async Task Stream_SingleNeverTerminatingDataLine_SurfacesOverflowError()
    {
        var prefix = Encoding.UTF8.GetBytes("data: {\"type\":\"response.output_text.delta\",\"delta\":\"");
        var junk = Encoding.ASCII.GetBytes(new string('Z', 10 * 1024 * 1024)); // 10 MiB, no '\n'
        var body = new byte[prefix.Length + junk.Length];
        Buffer.BlockCopy(prefix, 0, body, 0, prefix.Length);
        Buffer.BlockCopy(junk, 0, body, prefix.Length, junk.Length);

        var result = await RunAsync(HttpStatusCode.OK, new MemoryStream(body));

        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("limit", Case.Insensitive);
    }

    [Fact]
    public async Task Stream_OverCapErrorBody_DoesNotBufferUnbounded()
    {
        var huge = Encoding.ASCII.GetBytes(new string('e', 20 * 1024 * 1024)); // 20 MiB error body

        var result = await RunAsync(HttpStatusCode.InternalServerError, new MemoryStream(huge));

        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldNotBeNull();
    }

    [Fact]
    public async Task Stream_NormalWellFormedStream_ParsesUnaffected()
    {
        var body = "data: hello\ndata: world\n";

        var result = await RunAsync(HttpStatusCode.OK, new MemoryStream(Encoding.UTF8.GetBytes(body)));

        result.StopReason.ShouldNotBe(StopReason.Error);
        result.ErrorMessage.ShouldBeNull();
        result.Content.ShouldNotBeEmpty();
    }

    private static async Task<AssistantMessage> RunAsync(HttpStatusCode status, Stream body)
    {
        var handler = new StreamingHandler(status, body);
        var profile = new ResponsesTransportProfile(
            Api: "openai-responses",
            ActivityName: "test.responses.stream",
            BuildPayload: (_, _, _, _, _) => new JsonObject { ["model"] = "gpt-guard", ["stream"] = true },
            // Drain the reader exactly as the real parser does; the wrapped stream trips on overflow
            // before any line completes. On a well-formed body, accumulate text and finish.
            Parse: async (stream, reader, model, _, api, _, ct) =>
            {
                var sb = new StringBuilder();
                while (await reader.ReadLineAsync(ct) is { } l)
                    if (l.StartsWith("data: ", StringComparison.Ordinal))
                        sb.Append(l[6..]);
                var msg = new AssistantMessage(
                    Content: [new TextContent(sb.ToString())], Api: api, Provider: model.Provider,
                    ModelId: model.Id, Usage: Usage.Empty(), StopReason: StopReason.Stop,
                    ErrorMessage: null, ResponseId: null,
                    Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                stream.Push(new DoneEvent(StopReason.Stop, msg));
                stream.End(msg);
            },
            DecorateHeaders: (_, _, _, _) => { },
            ThrowForError: (resp, body) => throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {body}"));

        var stream = ResponsesStreamEngine.StreamAsync(
            profile, new HttpClient(handler), NullLogger.Instance, Model(),
            new Context(SystemPrompt: "guard", Messages: [new UserMessage(new UserMessageContent("guard"), 0)]),
            new StreamOptions { ApiKey = "test-key" });
        return await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class StreamingHandler(HttpStatusCode status, Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status) { Content = new StreamContent(body) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }

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

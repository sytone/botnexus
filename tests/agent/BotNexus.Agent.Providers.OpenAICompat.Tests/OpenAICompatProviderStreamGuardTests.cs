using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.OpenAICompat.Tests;

/// <summary>
/// Guards the OpenAI-compatible streaming transport against unbounded body reads (OOM-DoS,
/// issue #1685). Mirrors the merged Copilot guard (#1668): the untrusted SSE success stream is
/// wrapped in <see cref="ByteCountingStream"/> (16 MiB total + 8 MiB per-frame) and the non-2xx
/// error body is read via <see cref="BoundedHttpContent.ReadStringWithLimitAsync"/>. A hostile or
/// MITM'd OpenAI-compatible endpoint (Ollama/vLLM/LM Studio) can stream a never-ending success
/// body, a single never-terminating <c>data:</c> line, or a multi-GB error body; all three must
/// abort mid-flight. Well-formed under-cap streams must parse unchanged.
/// </summary>
public class OpenAICompatProviderStreamGuardTests
{
    private const long TotalCap = BoundedHttpContent.DefaultMaxResponseBytes; // 16 MiB

    [Fact]
    public async Task Stream_OverCapTotalSuccessBody_SurfacesOverflowError()
    {
        var line = ": " + new string('p', 1021) + "\n";
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
        var prefix = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"");
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
        var huge = Encoding.ASCII.GetBytes(new string('e', 20 * 1024 * 1024)); // 20 MiB error body

        var result = await RunProviderAsync(HttpStatusCode.InternalServerError, new MemoryStream(huge));

        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldNotBeNull();
    }

    [Fact]
    public async Task Stream_NormalWellFormedStream_ParsesUnaffected()
    {
        var body =
            "data: {\"choices\":[{\"delta\":{\"content\":\"hel\"}}]}\n" +
            "\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n" +
            "\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}\n" +
            "\n" +
            "data: [DONE]\n";

        var result = await RunProviderAsync(HttpStatusCode.OK, new MemoryStream(Encoding.UTF8.GetBytes(body)));

        result.StopReason.ShouldNotBe(StopReason.Error);
        result.ErrorMessage.ShouldBeNull();
        result.Content.ShouldNotBeEmpty();
    }

    private static async Task<AssistantMessage> RunProviderAsync(HttpStatusCode status, Stream body)
    {
        var handler = new StreamingHandler(status, body);
        var provider = new OpenAICompatProvider(new HttpClient(handler));
        var model = new LlmModel(
            Id: "compat-guard-test",
            Name: "compat-guard-test",
            Api: "openai-compat",
            Provider: "ollama",
            BaseUrl: "http://localhost:11434/v1",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 8192,
            MaxTokens: 2048);
        var context = new Context(
            SystemPrompt: "guard",
            Messages: [new UserMessage(new UserMessageContent("guard"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);
        var stream = provider.Stream(model, context, new Core.SimpleStreamOptions { ApiKey = "test-key" });
        return await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class StreamingHandler(HttpStatusCode status, Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
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

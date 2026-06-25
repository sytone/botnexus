using System.Net;
using BotNexus.Agent.Providers.OpenAICompat;

namespace BotNexus.Agent.Providers.OpenAICompat.Tests;

/// <summary>
/// Covers the bounded read + case-insensitive deserialization of the Ollama <c>/api/show</c>
/// capability probe (see <c>BoundedHttpContent</c>). Each test uses a unique base URL because the
/// checker caches results in a process-wide static dictionary keyed on base URL + model.
/// </summary>
public class OllamaCapabilityCheckerTests
{
    [Fact]
    public async Task SupportsToolsAsync_WithLowercaseCapabilitiesField_ReturnsTrue()
    {
        // Ollama returns lowercase JSON ("capabilities"); the bounded reader must keep the
        // case-insensitive (web-default) matching that ReadFromJsonAsync provided.
        var handler = new StubHandler(HttpStatusCode.OK, """{"capabilities":["completion","tools"]}""");
        var client = new HttpClient(handler);

        var result = await OllamaCapabilityChecker.SupportsToolsAsync(
            client, "http://ollama-lowercase-1:11434/v1", "llama3.1", CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task SupportsToolsAsync_WithoutToolsCapability_ReturnsFalse()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"capabilities":["completion"]}""");
        var client = new HttpClient(handler);

        var result = await OllamaCapabilityChecker.SupportsToolsAsync(
            client, "http://ollama-notools-1:11434/v1", "llama3.1", CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task SupportsToolsAsync_WithUnboundedResponseBody_DegradesToFalseWithoutOom()
    {
        // A hostile/misbehaving endpoint streaming an unbounded body must be aborted by the bounded
        // reader; the checker's catch then yields the safe default (no tools) rather than OOMing.
        using var stream = new NeverEndingStream();
        var handler = new StreamingStubHandler(stream);
        var client = new HttpClient(handler);

        var result = await OllamaCapabilityChecker.SupportsToolsAsync(
            client, "http://ollama-unbounded-1:11434/v1", "llama3.1", CancellationToken.None);

        result.ShouldBeFalse();
        // Never drained the infinite body — bounded to roughly the cap plus one chunk.
        stream.BytesRead.ShouldBeLessThan(
            BotNexus.Agent.Providers.Core.Utilities.BoundedHttpContent.DefaultMaxResponseBytes + (1024L * 1024));
    }

    [Fact]
    public async Task SupportsToolsAsync_WithNonSuccessStatus_ReturnsFalse()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, """{"error":"model not found"}""");
        var client = new HttpClient(handler);

        var result = await OllamaCapabilityChecker.SupportsToolsAsync(
            client, "http://ollama-404-1:11434/v1", "missing", CancellationToken.None);

        result.ShouldBeFalse();
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class StreamingStubHandler(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                // No Content-Length — forces the streaming read path to enforce the cap.
                Content = new StreamContent(stream)
            });
    }

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

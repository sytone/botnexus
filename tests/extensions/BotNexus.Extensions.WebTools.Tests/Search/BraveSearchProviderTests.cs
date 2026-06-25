using System.Net;
using BotNexus.Agent.Providers.Core.Utilities;
using BotNexus.Extensions.WebTools.Search;
using BotNexus.Extensions.WebTools.Tests.Helpers;

namespace BotNexus.Extensions.WebTools.Tests.Search;

[Trait("Category", "Unit")]
public class BraveSearchProviderTests
{
    [Fact]
    public async Task SearchAsync_WithValidResponse_MapsResults()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, """
            {
              "web": {
                "results": [
                  { "title": "A", "url": "https://example.com/a", "description": "Alpha" },
                  { "title": "B", "url": "https://example.com/b", "description": "Beta" }
                ]
              }
            }
            """);
        var provider = new BraveSearchProvider(new HttpClient(handler), "api-key");

        var results = await provider.SearchAsync("query", 5, CancellationToken.None);

        results.Count().ShouldBe(2);
        results[0].Title.ShouldBe("A");
        results[0].Url.ShouldBe("https://example.com/a");
        results[0].Snippet.ShouldBe("Alpha");
    }

    [Fact]
    public async Task SearchAsync_WithHttp401_ThrowsAuthError()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.Unauthorized, """{"error":"invalid key"}""");
        var provider = new BraveSearchProvider(new HttpClient(handler), "bad-key");

        var act = () => provider.SearchAsync("query", 5, CancellationToken.None);

        var ex = await act.ShouldThrowAsync<HttpRequestException>();
        ex.Message.ShouldContain("401");
    }

    [Fact]
    public async Task SearchAsync_WithHttp429_ThrowsRateLimitError()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse((HttpStatusCode)429, """{"error":"rate limit"}""");
        var provider = new BraveSearchProvider(new HttpClient(handler), "api-key");

        var act = () => provider.SearchAsync("query", 5, CancellationToken.None);

        var ex = await act.ShouldThrowAsync<HttpRequestException>();
        ex.Message.ShouldContain("429");
    }

    [Fact]
    public async Task SearchAsync_WithMalformedJson_ThrowsJsonException()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, "{not-json");
        var provider = new BraveSearchProvider(new HttpClient(handler), "api-key");

        var act = () => provider.SearchAsync("query", 5, CancellationToken.None);

        await act.ShouldThrowAsync<System.Text.Json.JsonException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task SearchAsync_WithMissingApiKey_StillExecutesRequest(string? apiKey)
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, """{"web":{"results":[]}}""");
        var provider = new BraveSearchProvider(new HttpClient(handler), apiKey!);

        var results = await provider.SearchAsync("query", 5, CancellationToken.None);
        results.ShouldBeEmpty();
        handler.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SearchAsync_WithUnboundedResponseBody_AbortsBeforeOom()
    {
        // A hostile/misbehaving search upstream streaming an unbounded body must be aborted by the
        // bounded reader rather than buffered whole. Proves the provider routes through the cap.
        using var stream = new NeverEndingStream();
        var handler = new StreamingResponseHandler(stream);
        var provider = new BraveSearchProvider(new HttpClient(handler), "api-key");

        var act = () => provider.SearchAsync("query", 5, CancellationToken.None);

        await act.ShouldThrowAsync<ResponseContentTooLargeException>();
        // Far less than the 16 MiB default cap's worth past one chunk — never the infinite body.
        stream.BytesRead.ShouldBeLessThan(BoundedHttpContent.DefaultMaxResponseBytes + (1024L * 1024));
    }

    private sealed class StreamingResponseHandler(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // No Content-Length — forces the streaming read path to enforce the cap.
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            return Task.FromResult(response);
        }
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

using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace BotNexus.Extensions.Channels.Matrix.Tests;

/// <summary>
/// Pins the authenticated Matrix media repository requests and both download size fences.
/// </summary>
public sealed class MatrixHttpClientMediaTests
{
    private const string Token = "syt_media_access_token";

    [Fact]
    public async Task UploadMediaAsync_PostsExactBodyAndContentTypeAndReturnsContentUri()
    {
        byte[] bytes = [0x00, 0x7F, 0x80, 0xFF];
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"content_uri\":\"mxc://media.example.com/a1\"}", Encoding.UTF8, "application/json"),
        });
        var client = CreateClient(handler);

        var contentUri = await client.UploadMediaAsync(bytes, "image/png", "diagram final.png", CancellationToken.None);

        contentUri.ShouldBe("mxc://media.example.com/a1");
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri.ShouldBe(new Uri("https://matrix.example.com/_matrix/media/v3/upload?filename=diagram%20final.png"));
        request.Authorization.ShouldBe(new AuthenticationHeaderValue("Bearer", Token));
        request.ContentType.ShouldBe("image/png");
        request.Body.ShouldBe(bytes);
    }

    [Fact]
    public async Task DownloadMediaAsync_GetsAuthenticatedMediaAndReturnsExactBytes()
    {
        byte[] bytes = [1, 2, 3, 4];
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });
        var client = CreateClient(handler);

        var downloaded = await client.DownloadMediaAsync("media.example.com", "folder/item 1", 4, CancellationToken.None);

        downloaded.ShouldBe(bytes);
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Get);
        request.RequestUri.ShouldBe(new Uri("https://matrix.example.com/_matrix/client/v1/media/download/media.example.com/folder%2Fitem%201"));
        request.Authorization.ShouldBe(new AuthenticationHeaderValue("Bearer", Token));
    }

    [Fact]
    public async Task DownloadMediaAsync_ContentLengthAboveCap_RejectsBeforeReadingBody()
    {
        var content = new TrackingContent([1, 2, 3, 4]);
        content.Headers.ContentLength = 4;
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        var client = CreateClient(handler);

        Func<Task> act = () => client.DownloadMediaAsync("media.example.com", "large", 3, CancellationToken.None);

        await Should.ThrowAsync<InvalidDataException>(act);
        content.StreamRequests.ShouldBe(0);
    }

    [Fact]
    public async Task DownloadMediaAsync_StreamAboveCap_StopsReadingAndRejects()
    {
        const int cap = 64 * 1024;
        var content = new TrackingContent(new byte[cap * 8]);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        var client = CreateClient(handler);

        Func<Task> act = () => client.DownloadMediaAsync("media.example.com", "lying-size", cap, CancellationToken.None);

        await Should.ThrowAsync<InvalidDataException>(act);
        content.BytesRead.ShouldBeGreaterThan(cap);
        content.BytesRead.ShouldBeLessThan(content.TotalBytes);
        content.BytesRead.ShouldBeLessThanOrEqualTo(cap * 2);
    }

    private static MatrixHttpClient CreateClient(RecordingHandler handler)
    {
        MatrixAccessToken.TryCreate(Token, out var accessToken).ShouldBeTrue();
        return new MatrixHttpClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://matrix.example.com/") },
            "@farnsworth:example.com",
            accessToken,
            secretRedactor: null);
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        AuthenticationHeaderValue? Authorization,
        string? ContentType,
        byte[] Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken)));
            return respond(request);
        }
    }

    private sealed class TrackingContent(byte[] bytes) : HttpContent
    {
        public int TotalBytes => bytes.Length;
        public int BytesRead { get; private set; }
        public int StreamRequests { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("The client must consume media as a stream.");

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            StreamRequests++;
            return Task.FromResult<Stream>(new TrackingReadStream(bytes, count => BytesRead += count));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class TrackingReadStream(byte[] bytes, Action<int> onRead) : MemoryStream(bytes, writable: false)
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            onRead(read);
            return read;
        }
    }
}

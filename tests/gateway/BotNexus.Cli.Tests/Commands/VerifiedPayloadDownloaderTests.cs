using System.Net;
using System.Security.Cryptography;
using System.Text;
using BotNexus.Cli.Commands;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Guard-rail tests for the fail-closed, checksum-verified payload downloader (issue #2372).
/// Every path that fetches executable content must prove it landed intact and matched the
/// expected SHA-256 before anything is allowed to run it.
/// </summary>
public class VerifiedPayloadDownloaderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "botnexus-payload-tests",
        Guid.NewGuid().ToString("N"));

    public VerifiedPayloadDownloaderTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked temp file must not fail the suite.
        }
    }

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HttpClient ClientReturning(HttpResponseMessage response) =>
        new(new StubHandler(response));

    private static HttpResponseMessage Ok(byte[] body, long? contentLengthOverride = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
        };
        response.Content.Headers.ContentLength = contentLengthOverride ?? body.Length;
        return response;
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_WritesPayload_WhenChecksumMatches()
    {
        var payload = Encoding.UTF8.GetBytes("#!/usr/bin/env bash\necho hello\n");
        using var client = ClientReturning(Ok(payload));
        var destination = Path.Combine(_tempDir, "payload.sh");

        var result = await VerifiedPayloadDownloader.DownloadAndVerifyAsync(
            client,
            new Uri("https://example.invalid/payload.sh"),
            Sha256Of(payload),
            destination,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Null(result.FailureReason);
        Assert.Equal(destination, result.FilePath);
        Assert.True(File.Exists(destination));
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_Fails_AndLeavesNoFile_WhenChecksumMismatches()
    {
        var payload = Encoding.UTF8.GetBytes("rm -rf /\n");
        var expected = Sha256Of(Encoding.UTF8.GetBytes("the script we actually trust"));
        using var client = ClientReturning(Ok(payload));
        var destination = Path.Combine(_tempDir, "tampered.sh");

        var result = await VerifiedPayloadDownloader.DownloadAndVerifyAsync(
            client,
            new Uri("https://example.invalid/tampered.sh"),
            expected,
            destination,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("checksum", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.FilePath);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_FailsClosed_OnNonSuccessStatus()
    {
        var response = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("<html>404</html>")
        };
        using var client = ClientReturning(response);
        var destination = Path.Combine(_tempDir, "missing.sh");

        var result = await VerifiedPayloadDownloader.DownloadAndVerifyAsync(
            client,
            new Uri("https://example.invalid/missing.sh"),
            Sha256Of(Encoding.UTF8.GetBytes("<html>404</html>")),
            destination,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("404", result.FailureReason);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_FailsClosed_OnTruncatedDownload()
    {
        var full = Encoding.UTF8.GetBytes("echo one\necho two\necho three\n");
        var truncated = full.AsSpan(0, 10).ToArray();

        // Server advertises the full length but only delivers a prefix.
        using var client = ClientReturning(Ok(truncated, contentLengthOverride: full.Length));
        var destination = Path.Combine(_tempDir, "truncated.sh");

        var result = await VerifiedPayloadDownloader.DownloadAndVerifyAsync(
            client,
            new Uri("https://example.invalid/truncated.sh"),
            Sha256Of(full),
            destination,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("truncated", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_Fails_WhenPayloadIsEmpty()
    {
        using var client = ClientReturning(Ok([]));
        var destination = Path.Combine(_tempDir, "empty.sh");

        var result = await VerifiedPayloadDownloader.DownloadAndVerifyAsync(
            client,
            new Uri("https://example.invalid/empty.sh"),
            Sha256Of([]),
            destination,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("empty", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hex-digest")]
    [InlineData("abcdef")]
    public async Task DownloadAndVerifyAsync_Rejects_MalformedExpectedChecksum(string expected)
    {
        var payload = Encoding.UTF8.GetBytes("echo hi\n");
        using var client = ClientReturning(Ok(payload));
        var destination = Path.Combine(_tempDir, "bad-digest.sh");

        var result = await VerifiedPayloadDownloader.DownloadAndVerifyAsync(
            client,
            new Uri("https://example.invalid/bad-digest.sh"),
            expected,
            destination,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("SHA-256", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_AcceptsUppercaseExpectedChecksum()
    {
        var payload = Encoding.UTF8.GetBytes("echo case-insensitive\n");
        using var client = ClientReturning(Ok(payload));
        var destination = Path.Combine(_tempDir, "upper.sh");

        var result = await VerifiedPayloadDownloader.DownloadAndVerifyAsync(
            client,
            new Uri("https://example.invalid/upper.sh"),
            Sha256Of(payload).ToUpperInvariant(),
            destination,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_FailsClosed_WhenTransportThrows()
    {
        using var client = new HttpClient(new ThrowingHandler());
        var destination = Path.Combine(_tempDir, "offline.sh");

        var result = await VerifiedPayloadDownloader.DownloadAndVerifyAsync(
            client,
            new Uri("https://example.invalid/offline.sh"),
            new string('a', 64),
            destination,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
        Assert.False(File.Exists(destination));
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("no route to host");
    }
}

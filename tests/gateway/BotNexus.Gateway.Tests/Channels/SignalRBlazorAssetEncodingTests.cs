using BotNexus.Extensions.Channels.SignalR;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace BotNexus.Gateway.Tests.Channels;

/// <summary>
/// Verifies the Blazor static-asset middleware prefers precompressed siblings
/// (.br/.gz) emitted by Blazor publish when the client advertises the matching
/// Accept-Encoding. This keeps the large runtime wasm/js small over the wire and
/// avoids mid-flight load failures on mobile over slow/proxied links.
/// </summary>
public sealed class SignalRBlazorAssetEncodingTests
{
    [Fact]
    public void SelectEncodedFile_PrefersBrotli_WhenClientAcceptsBr()
    {
        var provider = new StubFileProvider("/_framework/app.wasm.br", "/_framework/app.wasm.gz");

        var (file, encoding) = SignalREndpointContributor.SelectEncodedFile(
            provider, "/_framework/app.wasm", "gzip, deflate, br");

        encoding.ShouldBe("br");
        file.ShouldNotBeNull();
        file!.Name.ShouldEndWith(".br");
    }

    [Fact]
    public void SelectEncodedFile_FallsBackToGzip_WhenBrNotAccepted()
    {
        var provider = new StubFileProvider("/_framework/app.wasm.br", "/_framework/app.wasm.gz");

        var (file, encoding) = SignalREndpointContributor.SelectEncodedFile(
            provider, "/_framework/app.wasm", "gzip, deflate");

        encoding.ShouldBe("gzip");
        file!.Name.ShouldEndWith(".gz");
    }

    [Fact]
    public void SelectEncodedFile_ReturnsNull_WhenNoCompressedSiblingExists()
    {
        var provider = new StubFileProvider();

        var (file, encoding) = SignalREndpointContributor.SelectEncodedFile(
            provider, "/_framework/app.wasm", "gzip, br");

        file.ShouldBeNull();
        encoding.ShouldBeNull();
    }

    [Fact]
    public void SelectEncodedFile_ReturnsNull_WhenClientAcceptsNoEncoding()
    {
        var provider = new StubFileProvider("/_framework/app.wasm.br");

        var (file, encoding) = SignalREndpointContributor.SelectEncodedFile(
            provider, "/_framework/app.wasm", StringValues.Empty);

        file.ShouldBeNull();
        encoding.ShouldBeNull();
    }

    [Fact]
    public void SelectEncodedFile_DoesNotDoubleCompress_AlreadyCompressedRequest()
    {
        var provider = new StubFileProvider("/_framework/app.wasm.br.br");

        var (file, encoding) = SignalREndpointContributor.SelectEncodedFile(
            provider, "/_framework/app.wasm.br", "br");

        file.ShouldBeNull();
        encoding.ShouldBeNull();
    }

    private sealed class StubFileProvider(params string[] existingPaths) : IFileProvider
    {
        private readonly HashSet<string> _existing = new(existingPaths, StringComparer.Ordinal);

        public IFileInfo GetFileInfo(string subpath) =>
            _existing.Contains(subpath)
                ? new StubFileInfo(subpath)
                : new NotFoundFileInfo(subpath);

        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;
        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
    }

    private sealed class StubFileInfo(string subpath) : IFileInfo
    {
        public bool Exists => true;
        public long Length => 42;
        public string? PhysicalPath => null;
        public string Name => subpath[(subpath.LastIndexOf('/') + 1)..];
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public bool IsDirectory => false;
        public Stream CreateReadStream() => new MemoryStream(new byte[42]);
    }
}

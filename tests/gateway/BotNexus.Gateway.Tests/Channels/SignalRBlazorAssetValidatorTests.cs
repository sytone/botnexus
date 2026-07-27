using BotNexus.Extensions.Channels.SignalR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace BotNexus.Gateway.Tests.Channels;

/// <summary>
/// Verifies the revalidated (non-fingerprinted) half of the Blazor static-asset middleware
/// emits a usable cache validator. <c>Cache-Control: no-cache</c> only *permits* a conditional
/// GET -- without an ETag or Last-Modified the browser has nothing to put in If-None-Match, so
/// every revalidation re-downloads the full body instead of settling as a 304. See #2413.
/// </summary>
public sealed class SignalRBlazorAssetValidatorTests
{
    private static readonly DateTimeOffset Modified =
        new(2026, 7, 27, 14, 30, 15, TimeSpan.Zero);

    [Fact]
    public void BuildEntityTag_IsWeak_AndDerivedFromLastModifiedAndLength()
    {
        var tag = SignalREndpointContributor.BuildEntityTag(new StubFileInfo("app.js", 1234, Modified));

        // Weak is correct: last-write + length identifies the deployed bytes without
        // hashing megabytes of wasm on every single request.
        tag.ShouldStartWith("W/\"");
        tag.ShouldEndWith("\"");
        tag.ShouldBe($"W/\"{Modified.Ticks:x}-{1234:x}\"");
    }

    [Fact]
    public void BuildEntityTag_DiffersPerEncoding_SoBrAndGzNeverShareATag()
    {
        // The tag must come from the file ACTUALLY served (the .br/.gz sibling), never the
        // identity file -- otherwise two different response bodies claim the same validator
        // and a shared cache can hand a br body to a gzip-only client.
        var identity = SignalREndpointContributor.BuildEntityTag(new StubFileInfo("app.js", 4096, Modified));
        var brotli = SignalREndpointContributor.BuildEntityTag(new StubFileInfo("app.js.br", 900, Modified));

        brotli.ShouldNotBe(identity);
    }

    [Fact]
    public void IsNotModified_ReturnsTrue_WhenIfNoneMatchEchoesTheTag()
    {
        var tag = SignalREndpointContributor.BuildEntityTag(new StubFileInfo("app.js", 42, Modified));

        SignalREndpointContributor.IsNotModified(Request(ifNoneMatch: tag), tag, Modified)
            .ShouldBeTrue();
    }

    [Fact]
    public void IsNotModified_ReturnsTrue_WhenTagAppearsInAMultiValueList()
    {
        var tag = SignalREndpointContributor.BuildEntityTag(new StubFileInfo("app.js", 42, Modified));

        SignalREndpointContributor.IsNotModified(Request(ifNoneMatch: $"W/\"stale\", {tag}"), tag, Modified)
            .ShouldBeTrue();
    }

    [Fact]
    public void IsNotModified_ReturnsTrue_ForWildcard()
    {
        SignalREndpointContributor.IsNotModified(Request(ifNoneMatch: "*"), "W/\"abc\"", Modified)
            .ShouldBeTrue();
    }

    [Fact]
    public void IsNotModified_ReturnsFalse_WhenTagDoesNotMatch()
    {
        // A redeployed asset changes length and/or timestamp, so the stale tag must miss and
        // the client must receive the new body.
        SignalREndpointContributor.IsNotModified(Request(ifNoneMatch: "W/\"0-0\""), "W/\"abc-1\"", Modified)
            .ShouldBeFalse();
    }

    [Fact]
    public void IsNotModified_ReturnsFalse_WhenNoConditionalHeadersSent()
    {
        SignalREndpointContributor.IsNotModified(Request(), "W/\"abc-1\"", Modified)
            .ShouldBeFalse();
    }

    [Fact]
    public void IsNotModified_PrefersEntityTagOverDate_WhenBothPresent()
    {
        // RFC 9110: an entity-tag comparison always wins over a date comparison. The date here
        // is newer than the file, which would say "not modified" on its own -- the mismatched
        // tag must still force a full response.
        var request = Request(ifNoneMatch: "W/\"stale\"", ifModifiedSince: Modified.AddHours(1).ToString("R"));

        SignalREndpointContributor.IsNotModified(request, "W/\"current\"", Modified)
            .ShouldBeFalse();
    }

    [Fact]
    public void IsNotModified_FallsBackToIfModifiedSince_WhenNoEntityTagSent()
    {
        var request = Request(ifModifiedSince: Modified.ToString("R"));

        // Sub-second precision must be truncated: HTTP-date has one-second resolution, so
        // comparing raw ticks would never match a date we just emitted ourselves.
        SignalREndpointContributor.IsNotModified(request, "W/\"any\"", Modified.AddMilliseconds(400))
            .ShouldBeTrue();
    }

    [Fact]
    public void IsNotModified_ReturnsFalse_WhenFileIsNewerThanIfModifiedSince()
    {
        var request = Request(ifModifiedSince: Modified.ToString("R"));

        SignalREndpointContributor.IsNotModified(request, "W/\"any\"", Modified.AddMinutes(5))
            .ShouldBeFalse();
    }

    [Fact]
    public void IsNotModified_ReturnsFalse_WhenIfModifiedSinceIsUnparseable()
    {
        // A malformed date must fail safe to "send the body", never to a bogus 304.
        SignalREndpointContributor.IsNotModified(Request(ifModifiedSince: "not-a-date"), "W/\"any\"", Modified)
            .ShouldBeFalse();
    }

    [Fact]
    public void RevalidateCacheControl_MatchesWhatResolveCacheControlEmits()
    {
        // The middleware gates validator emission on this exact constant, so a drift between
        // the constant and the emitted policy would silently stop ETags being sent.
        SignalREndpointContributor.ResolveCacheControl("/index.html")
            .ShouldBe(SignalREndpointContributor.RevalidateCacheControl);

        SignalREndpointContributor.ResolveCacheControl("/_framework/dotnet.native.veuqw8a0w9.wasm")
            .ShouldNotBe(SignalREndpointContributor.RevalidateCacheControl);
    }

    private static HttpRequest Request(string? ifNoneMatch = null, string? ifModifiedSince = null)
    {
        var context = new DefaultHttpContext();
        if (ifNoneMatch is not null)
            context.Request.Headers.IfNoneMatch = ifNoneMatch;
        if (ifModifiedSince is not null)
            context.Request.Headers.IfModifiedSince = ifModifiedSince;
        return context.Request;
    }

    private sealed class StubFileInfo(string name, long length, DateTimeOffset lastModified) : IFileInfo
    {
        public bool Exists => true;
        public long Length => length;
        public string? PhysicalPath => null;
        public string Name => name;
        public DateTimeOffset LastModified => lastModified;
        public bool IsDirectory => false;
        public Stream CreateReadStream() => new MemoryStream(new byte[length]);
    }
}

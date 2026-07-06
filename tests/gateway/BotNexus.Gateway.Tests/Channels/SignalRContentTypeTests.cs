using BotNexus.Extensions.Channels.SignalR;

namespace BotNexus.Gateway.Tests.Channels;

/// <summary>
/// Verifies the Blazor static-asset middleware maps file extensions to the
/// correct Content-Type. The <c>.webmanifest</c> mapping is the runtime seam
/// behind PWA installability: Chromium/Edge expect the web app manifest to be
/// served as <c>application/manifest+json</c>, and serving it as
/// <c>application/octet-stream</c> can cause the manifest to be rejected and the
/// install affordance suppressed (see issue #1776).
/// </summary>
public sealed class SignalRContentTypeTests
{
    [Fact]
    public void GetContentType_WebManifest_ReturnsManifestJson()
    {
        SignalREndpointContributor.GetContentType("manifest.webmanifest")
            .ShouldBe("application/manifest+json");
    }

    [Fact]
    public void GetContentType_Json_RemainsApplicationJson()
    {
        // The mobile client uses manifest.json; application/json is tolerated by
        // browsers, and other .json assets must not regress.
        SignalREndpointContributor.GetContentType("manifest.json")
            .ShouldBe("application/json");
    }

    [Fact]
    public void GetContentType_UnknownExtension_FallsBackToOctetStream()
    {
        SignalREndpointContributor.GetContentType("blob.unknownext")
            .ShouldBe("application/octet-stream");
    }
}

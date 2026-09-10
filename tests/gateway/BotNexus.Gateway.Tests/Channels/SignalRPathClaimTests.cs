using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Extensions;

namespace BotNexus.Gateway.Tests.Channels;

/// <summary>
/// Pins the portal catch-all yielding paths that other extensions declare.
/// </summary>
/// <remarks>
/// This exists because the behaviour it protects was broken in production and no test noticed.
/// Moving a carried extension behind authentication put its middleware after the portal's
/// catch-all, which registers in the pre-auth pass, so the portal answered first and the
/// extension's route returned the SPA document instead of its own. Contributor ordering cannot fix
/// that: it sorts within a pipeline phase, and these two are in different phases.
///
/// The load-bearing cases are the ones that would take the whole UI down or silently reinstate the
/// swallow: a claim of "/" must be ignored, a sibling path that merely shares a prefix must not be
/// claimed, and the portal must never claim a path against itself.
/// </remarks>
public sealed class SignalRPathClaimTests
{
    private static LoadedExtension Extension(string id, params string?[] navPaths) => new()
    {
        ExtensionId = id,
        Name = id,
        Version = "1.0.0",
        DirectoryPath = "/tmp/" + id,
        EntryAssemblyPath = "/tmp/" + id + "/x.dll",
        LoadedAtUtc = DateTimeOffset.UnixEpoch,
        Nav = [.. navPaths.Select(p => new ExtensionNavEntry
        {
            Id = "n" + Guid.NewGuid().ToString("N")[..6],
            Label = "N",
            Path = p!,
        })],
    };

    [Fact]
    public void Derives_a_claim_from_a_declared_nav_path()
    {
        var claims = SignalREndpointContributor.ResolveClaimedPaths(
            [Extension("botnexus-agent-builder", "/agent-builder")]);

        Assert.Equal(["/agent-builder"], claims);
    }

    // Claiming the root would make the portal itself unreachable - the one mistake here that takes
    // the whole UI down rather than one route.
    [Theory]
    [InlineData("/")]
    [InlineData("//")]
    public void Never_claims_the_root(string path)
    {
        Assert.Empty(SignalREndpointContributor.ResolveClaimedPaths([Extension("x", path)]));
    }

    // The portal is the fallback. A path it claimed against itself could never be served.
    [Fact]
    public void The_portal_does_not_claim_paths_against_itself()
    {
        var claims = SignalREndpointContributor.ResolveClaimedPaths(
            [Extension(SignalREndpointContributor.PortalExtensionId, "/anything")]);

        Assert.Empty(claims);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("agent-builder")]
    [InlineData("https://evil.example/x")]
    public void Ignores_a_path_that_is_not_site_relative(string? path)
    {
        Assert.Empty(SignalREndpointContributor.ResolveClaimedPaths([Extension("x", path)]));
    }

    [Fact]
    public void Normalises_a_trailing_slash_and_deduplicates()
    {
        var claims = SignalREndpointContributor.ResolveClaimedPaths(
        [
            Extension("a", "/thing/"),
            Extension("b", "/thing"),
        ]);

        Assert.Equal(["/thing"], claims);
    }

    // The claim covers the view and the assets it serves beneath itself.
    [Theory]
    [InlineData("/agent-builder")]
    [InlineData("/agent-builder/")]
    [InlineData("/agent-builder/assets/index.js")]
    public void Claims_the_path_and_everything_beneath_it(string requested)
    {
        Assert.True(SignalREndpointContributor.ClaimsPath(["/agent-builder"], requested));
    }

    // A sibling that merely shares a prefix is a DIFFERENT route. Matching it would hand an
    // extension paths it never declared.
    [Theory]
    [InlineData("/agent-builder-other")]
    [InlineData("/agent-builderx")]
    [InlineData("/agent")]
    [InlineData("/other")]
    [InlineData("/")]
    public void Does_not_claim_a_path_that_merely_shares_a_prefix(string requested)
    {
        Assert.False(SignalREndpointContributor.ClaimsPath(["/agent-builder"], requested));
    }

    [Fact]
    public void Claims_nothing_when_no_extension_declared_a_path()
    {
        Assert.False(SignalREndpointContributor.ClaimsPath([], "/agent-builder"));
    }

    // Matching is case-insensitive, as the surrounding passthrough checks are.
    [Fact]
    public void Claim_matching_ignores_case()
    {
        Assert.True(SignalREndpointContributor.ClaimsPath(["/agent-builder"], "/Agent-Builder/assets/x.js"));
    }
}

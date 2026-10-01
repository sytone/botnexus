using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace BotNexus.Gateway.Tests;

public sealed class ClaimsUserIdProviderTests
{
    private readonly ClaimsUserIdProvider _provider = new();

    [Fact]
    public void GetUserId_WithOidClaim_ReturnsOid()
    {
        var connection = CreateConnection(
            authenticated: true,
            new Claim(ClaimsUserIdProvider.OidClaimType, "oid-value-123"));

        var result = _provider.GetUserId(connection);

        result.ShouldBe("oid-value-123");
    }

    [Fact]
    public void GetUserId_WithShortOidClaim_ReturnsOid()
    {
        var connection = CreateConnection(
            authenticated: true,
            new Claim(ClaimsUserIdProvider.OidShortClaimType, "short-oid-456"));

        var result = _provider.GetUserId(connection);

        result.ShouldBe("short-oid-456");
    }

    [Fact]
    public void GetUserId_WithBlankOid_FallsBackToSub()
    {
        var connection = CreateConnection(
            authenticated: true,
            new Claim(ClaimsUserIdProvider.OidClaimType, "  "),
            new Claim(ClaimsUserIdProvider.SubClaimType, "sub-after-blank-oid"));

        var result = _provider.GetUserId(connection);

        result.ShouldBe("sub-after-blank-oid");
    }

    [Fact]
    public void GetUserId_WithSubClaim_WhenNoOid_ReturnsSub()
    {
        var connection = CreateConnection(
            authenticated: true,
            new Claim(ClaimsUserIdProvider.SubClaimType, "sub-value-789"));

        var result = _provider.GetUserId(connection);

        result.ShouldBe("sub-value-789");
    }

    [Fact]
    public void GetUserId_WithBothOidAndSub_PrefersOid()
    {
        var connection = CreateConnection(
            authenticated: true,
            new Claim(ClaimsUserIdProvider.OidClaimType, "oid-wins"),
            new Claim(ClaimsUserIdProvider.SubClaimType, "sub-loses"));

        var result = _provider.GetUserId(connection);

        result.ShouldBe("oid-wins");
    }

    [Fact]
    public void GetUserId_Unauthenticated_UsesServerOwnedLocalReader()
    {
        var firstConnection = CreateConnection(authenticated: false);
        var secondConnection = CreateConnection(authenticated: false);

        var first = _provider.GetUserId(firstConnection);
        var second = _provider.GetUserId(secondConnection);

        first.ShouldBe(ConversationReaderIdentity.LocalOwner.Value);
        second.ShouldBe(first);
        first.ShouldNotBe("test-connection");
    }

    [Fact]
    public void ResolveReaderId_WithAuthenticatedOid_ReturnsTypedReaderId()
    {
        var principal = CreatePrincipal(
            authenticated: true,
            new Claim(ClaimsUserIdProvider.OidClaimType, " reader-oid "),
            new Claim(ClaimsUserIdProvider.SubClaimType, "reader-sub"));

        var readerId = ConversationReaderIdentity.Resolve(principal);

        readerId.ShouldBe(ConversationReaderId.From("reader-oid"));
    }

    [Fact]
    public void ResolveReaderId_WithAuthenticatedIdentityMissingStableClaim_FailsClosed()
    {
        var principal = CreatePrincipal(authenticated: true, new Claim(ClaimTypes.Name, "display-name"));

        ConversationReaderIdentity.Resolve(principal).ShouldBeNull();
    }

    [Fact]
    public void GetUserId_NoClaims_ReturnsNull()
    {
        // Authenticated but no oid/sub claims
        var connection = CreateConnection(authenticated: true);

        var result = _provider.GetUserId(connection);

        result.ShouldBeNull();
    }

    /// <summary>
    /// Creates a <see cref="HubConnectionContext"/> backed by a test in-memory connection
    /// using the ASP.NET test infrastructure, with the given authentication state and claims.
    /// </summary>
    private static HubConnectionContext CreateConnection(bool authenticated, params Claim[] claims)
    {
        // Use the TestHubConnectionContext to avoid unmockable HubConnectionContext constructor
        return new TestHubConnectionContext(CreatePrincipal(authenticated, claims));
    }

    private static ClaimsPrincipal CreatePrincipal(bool authenticated, params Claim[] claims)
    {
        var identity = authenticated
            ? new ClaimsIdentity(claims, "Bearer")
            : new ClaimsIdentity(claims); // no authenticationType → IsAuthenticated = false
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Minimal HubConnectionContext subclass for testing ClaimsUserIdProvider.
    /// Only the User property is consumed by the provider.
    /// </summary>
    private sealed class TestHubConnectionContext : HubConnectionContext
    {
        private readonly ClaimsPrincipal _user;

        public TestHubConnectionContext(ClaimsPrincipal user)
            : base(new TestConnectionContext(), new HubConnectionContextOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(30),
                ClientTimeoutInterval = TimeSpan.FromSeconds(60)
            }, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
        {
            _user = user;
        }

        public override ClaimsPrincipal User => _user;
    }

    /// <summary>
    /// Minimal ConnectionContext implementation for test HubConnectionContext construction.
    /// </summary>
    private sealed class TestConnectionContext : Microsoft.AspNetCore.Connections.ConnectionContext
    {
        public override string ConnectionId { get; set; } = "test-connection";
        public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; }
            = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();
        public override System.IO.Pipelines.IDuplexPipe Transport { get; set; } = null!;
    }
}

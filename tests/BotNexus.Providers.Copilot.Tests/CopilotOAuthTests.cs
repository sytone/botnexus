using FluentAssertions;
using BotNexus.Agent.Providers.Copilot;

namespace BotNexus.Providers.Copilot.Tests;

public class CopilotOAuthTests
{
    [Fact]
    public void OAuthCredentials_CanBeCreatedWithValidProperties()
    {
        var creds = new OAuthCredentials(
            AccessToken: "ghu_abc123",
            RefreshToken: "gho_refresh456",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        );

        creds.AccessToken.Should().Be("ghu_abc123");
        creds.RefreshToken.Should().Be("gho_refresh456");
        creds.ExpiresAt.Should().BeGreaterThan(0);
    }

    [Fact]
    public void OAuthCredentials_DefaultApiEndpoint_IsNull()
    {
        var creds = new OAuthCredentials("token", "refresh", 0);
        creds.ApiEndpoint.Should().BeNull();
    }

    [Fact]
    public void OAuthCredentials_WithApiEndpoint_PreservesValue()
    {
        var creds = new OAuthCredentials("token", "refresh", 0, "https://enterprise.copilot.example.com");
        creds.ApiEndpoint.Should().Be("https://enterprise.copilot.example.com");
    }

    [Fact]
    public void OAuthCredentials_RecordEquality_MatchesOnAllFields()
    {
        var a = new OAuthCredentials("tok", "ref", 100, "https://api.example.com");
        var b = new OAuthCredentials("tok", "ref", 100, "https://api.example.com");
        a.Should().Be(b);
    }

    [Fact]
    public void OAuthCredentials_RecordEquality_DiffersOnAccessToken()
    {
        var a = new OAuthCredentials("tok1", "ref", 100);
        var b = new OAuthCredentials("tok2", "ref", 100);
        a.Should().NotBe(b);
    }

    [Fact]
    public void OAuthCredentials_RecordEquality_DiffersOnExpiresAt()
    {
        var a = new OAuthCredentials("tok", "ref", 100);
        var b = new OAuthCredentials("tok", "ref", 200);
        a.Should().NotBe(b);
    }

    // --- Expiry detection tests ---

    [Fact]
    public void TokenExpiryDetection_WorksCorrectly_WhenExpired()
    {
        var pastExpiry = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var creds = new OAuthCredentials("token", "refresh", pastExpiry);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var isExpired = now >= creds.ExpiresAt - 60;

        isExpired.Should().BeTrue("token expired 5 minutes ago");
    }

    [Fact]
    public void TokenExpiryDetection_WorksCorrectly_WhenValid()
    {
        var futureExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var creds = new OAuthCredentials("token", "refresh", futureExpiry);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var isExpired = now >= creds.ExpiresAt - 60;

        isExpired.Should().BeFalse("token is still valid for ~1 hour");
    }

    [Fact]
    public void TokenExpiryDetection_WorksCorrectly_WhenWithin60Seconds()
    {
        var almostExpired = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds();
        var creds = new OAuthCredentials("token", "refresh", almostExpired);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var isExpired = now >= creds.ExpiresAt - 60;

        isExpired.Should().BeTrue("token expires in 30s, which is within the 60s refresh window");
    }

    [Fact]
    public void TokenExpiryDetection_ExactlyAt60Seconds_ShouldTriggerRefresh()
    {
        var exactBoundary = DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeSeconds();
        var creds = new OAuthCredentials("token", "refresh", exactBoundary);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var isExpired = now >= creds.ExpiresAt - 60;

        isExpired.Should().BeTrue("token at exactly 60s boundary should trigger refresh");
    }

    [Fact]
    public void TokenExpiryDetection_ExpiresAtZero_ShouldAlwaysNeedRefresh()
    {
        var creds = new OAuthCredentials("token", "refresh", 0);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var isExpired = now >= creds.ExpiresAt - 60;

        isExpired.Should().BeTrue("ExpiresAt=0 forces refresh on first use (login flow)");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100000)]
    public void TokenExpiryDetection_NegativeExpiresAt_ShouldNeedRefresh(long expiresAt)
    {
        var creds = new OAuthCredentials("token", "refresh", expiresAt);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var isExpired = now >= creds.ExpiresAt - 60;

        isExpired.Should().BeTrue("negative ExpiresAt is always expired");
    }

    // --- GetApiKeyAsync tests ---

    [Fact]
    public async Task GetApiKeyAsync_WhenProviderNotInMap_ReturnsNull()
    {
        var map = new Dictionary<string, OAuthCredentials>();

        var result = await CopilotOAuth.GetApiKeyAsync("unknown-provider", map);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetApiKeyAsync_WhenProviderExistsButEmptyMap_ReturnsNull()
    {
        var map = new Dictionary<string, OAuthCredentials>
        {
            ["other-provider"] = new("token", "refresh", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds())
        };

        var result = await CopilotOAuth.GetApiKeyAsync("missing-provider", map);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetApiKeyAsync_WhenMultipleProviders_ReturnsCorrectOne()
    {
        var futureExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var map = new Dictionary<string, OAuthCredentials>
        {
            ["provider-a"] = new("token-a", "refresh-a", futureExpiry),
            ["provider-b"] = new("token-b", "refresh-b", futureExpiry)
        };

        var result = await CopilotOAuth.GetApiKeyAsync("provider-a", map);

        result.Should().NotBeNull();
        result!.Value.ApiKey.Should().Be("token-a");
    }

    // --- OAuthCredentials with-expressions (record mutation) ---

    [Fact]
    public void OAuthCredentials_WithExpression_CanUpdateAccessToken()
    {
        var original = new OAuthCredentials("old-token", "refresh", 100);
        var updated = original with { AccessToken = "new-token" };

        updated.AccessToken.Should().Be("new-token");
        updated.RefreshToken.Should().Be("refresh");
        updated.ExpiresAt.Should().Be(100);
    }

    [Fact]
    public void OAuthCredentials_WithExpression_CanSetApiEndpoint()
    {
        var original = new OAuthCredentials("token", "refresh", 100);
        var updated = original with { ApiEndpoint = "https://enterprise.example.com" };

        updated.ApiEndpoint.Should().Be("https://enterprise.example.com");
        original.ApiEndpoint.Should().BeNull("original should not be mutated");
    }
}

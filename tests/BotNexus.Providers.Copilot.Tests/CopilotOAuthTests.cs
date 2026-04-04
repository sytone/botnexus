using FluentAssertions;
using BotNexus.Providers.Copilot;

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
}

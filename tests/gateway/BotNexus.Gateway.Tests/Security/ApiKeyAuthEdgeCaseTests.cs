using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Security;

[Trait("Category", "Security")]
public sealed class ApiKeyAuthEdgeCaseTests
{
    [Fact]
    public async Task AuthenticateAsync_WithEmptyApiKeyHeader_IsRejected()
    {
        var handler = new ApiKeyGatewayAuthHandler("secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext([new KeyValuePair<string, string>("X-Api-Key", string.Empty)]));

        result.IsAuthenticated.ShouldBeFalse();
        result.FailureReason.ShouldStartWith("Missing API key");
    }

    [Fact]
    public async Task AuthenticateAsync_WithWhitespaceApiKeyHeader_IsRejected()
    {
        var handler = new ApiKeyGatewayAuthHandler("secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext([new KeyValuePair<string, string>("X-Api-Key", "   ")]));

        result.IsAuthenticated.ShouldBeFalse();
        result.FailureReason.ShouldStartWith("Missing API key");
    }

    [Fact]
    public async Task AuthenticateAsync_WithExtremelyLongApiKey_RejectsWhenNotConfigured()
    {
        var handler = new ApiKeyGatewayAuthHandler("secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance);
        var longToken = new string('x', 10_000);

        var result = await handler.AuthenticateAsync(CreateContext([new KeyValuePair<string, string>("X-Api-Key", longToken)]));

        result.IsAuthenticated.ShouldBeFalse();
        result.FailureReason.ShouldBe("Invalid API key.");
    }

    [Fact]
    public async Task AuthenticateAsync_WithExtremelyLongApiKey_AllowsWhenConfigured()
    {
        var longToken = new string('x', 10_000);
        var handler = new ApiKeyGatewayAuthHandler(longToken, NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext([new KeyValuePair<string, string>("X-Api-Key", longToken)]));

        result.IsAuthenticated.ShouldBeTrue();
        result.Identity!.CallerId.ShouldBe("gateway-api-key");
    }

    [Fact]
    public async Task AuthenticateAsync_WithEmptyBearerToken_IsRejected()
    {
        var handler = new ApiKeyGatewayAuthHandler("secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext([new KeyValuePair<string, string>("Authorization", "Bearer ")]));

        result.IsAuthenticated.ShouldBeFalse();
        result.FailureReason.ShouldStartWith("Missing API key");
    }

    [Fact]
    public async Task AuthenticateAsync_WithMalformedAuthorizationHeader_IsRejected()
    {
        var handler = new ApiKeyGatewayAuthHandler("secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext([new KeyValuePair<string, string>("Authorization", "Bearertoken123")]));

        result.IsAuthenticated.ShouldBeFalse();
        result.FailureReason.ShouldStartWith("Missing API key");
    }

    [Fact]
    public async Task AuthenticateAsync_WithNullByteInApiKey_IsRejected()
    {
        var handler = new ApiKeyGatewayAuthHandler("secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext([new KeyValuePair<string, string>("X-Api-Key", "secret\0") ]));

        result.IsAuthenticated.ShouldBeFalse();
        result.FailureReason.ShouldBe("Invalid API key.");
    }

    [Fact]
    public async Task AuthenticateAsync_ConcurrentCalls_WithDifferentTokens_ResolveCorrectly()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                ApiKeys = new Dictionary<string, ApiKeyConfig>
                {
                    ["tenant-a"] = new() { ApiKey = "token-a", CallerId = "caller-a", TenantId = "tenant-a", Permissions = [] },
                    ["tenant-b"] = new() { ApiKey = "token-b", CallerId = "caller-b", TenantId = "tenant-b", Permissions = [] }
                }
            }
        };
        var handler = new ApiKeyGatewayAuthHandler(config, NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var firstAuthTask = handler.AuthenticateAsync(CreateContext([new KeyValuePair<string, string>("X-Api-Key", "token-a") ]));
        var secondAuthTask = handler.AuthenticateAsync(CreateContext([new KeyValuePair<string, string>("Authorization", "Bearer token-b") ]));
        var results = await Task.WhenAll(firstAuthTask, secondAuthTask);

        results.ShouldAllBe(r => r.IsAuthenticated);
        results.Select(r => r.Identity!.CallerId).ShouldBe(["caller-a", "caller-b"]);
    }

    private static GatewayAuthContext CreateContext(IEnumerable<KeyValuePair<string, string>> headers)
        => new()
        {
            Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
            QueryParameters = new Dictionary<string, string>(),
            Path = "/api/messages",
            Method = "POST"
        };
}

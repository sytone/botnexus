using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class ApiKeyGatewayAuthHandlerTests
{
    [Fact]
    public async Task AuthenticateAsync_WithoutConfiguredKey_ReturnsDevelopmentIdentity()
    {
        var handler = new ApiKeyGatewayAuthHandler(apiKey: null, NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext());

        result.IsAuthenticated.Should().BeTrue();
        result.Identity!.CallerId.Should().Be("gateway-dev");
    }

    [Fact]
    public async Task AuthenticateAsync_WithMissingHeaders_ReturnsFailure()
    {
        var handler = new ApiKeyGatewayAuthHandler("secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext());

        result.IsAuthenticated.Should().BeFalse();
        result.FailureReason.Should().StartWith("Missing API key");
    }

    [Fact]
    public async Task AuthenticateAsync_WithInvalidApiKey_ReturnsFailure()
    {
        var handler = new ApiKeyGatewayAuthHandler("secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext(new Dictionary<string, string> { ["X-Api-Key"] = "wrong" }));

        result.IsAuthenticated.Should().BeFalse();
        result.FailureReason.Should().Be("Invalid API key.");
    }

    [Fact]
    public async Task AuthenticateAsync_WithBearerHeader_ReturnsSuccess()
    {
        var handler = new ApiKeyGatewayAuthHandler("secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext(new Dictionary<string, string> { ["Authorization"] = "Bearer secret" }));

        result.IsAuthenticated.Should().BeTrue();
        result.Identity!.CallerId.Should().Be("gateway-api-key");
    }

    [Fact]
    public async Task AuthenticateAsync_WithConfiguredTenantApiKeys_ReturnsTenantIdentity()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                ApiKeys = new Dictionary<string, ApiKeyConfig>
                {
                    ["tenant-a"] = new()
                    {
                        ApiKey = "tenant-a-secret",
                        TenantId = "tenant-a",
                        CallerId = "caller-a",
                        Permissions = ["chat:send", "sessions:read"],
                        AllowedAgents = ["assistant-a"]
                    }
                }
            }
        };
        var handler = new ApiKeyGatewayAuthHandler(config, NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext(new Dictionary<string, string> { ["X-Api-Key"] = "tenant-a-secret" }));

        result.IsAuthenticated.Should().BeTrue();
        result.Identity!.CallerId.Should().Be("caller-a");
        result.Identity.TenantId.Should().Be("tenant-a");
        result.Identity.Permissions.Should().Contain("chat:send");
        result.Identity.AllowedAgents.Should().ContainSingle().Which.Should().Be("assistant-a");
    }

    [Fact]
    public async Task AuthenticateAsync_WithApiKeyHeader_ReturnsSuccess()
    {
        var handler = new ApiKeyGatewayAuthHandler("secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext(new Dictionary<string, string> { ["x-api-key"] = "secret" }));

        result.IsAuthenticated.Should().BeTrue();
        result.Identity!.CallerId.Should().Be("gateway-api-key");
    }

    private static GatewayAuthContext CreateContext(IReadOnlyDictionary<string, string>? headers = null)
        => new()
        {
            Headers = headers ?? new Dictionary<string, string>(),
            QueryParameters = new Dictionary<string, string>(),
            Path = "/api/messages",
            Method = "POST"
        };
}

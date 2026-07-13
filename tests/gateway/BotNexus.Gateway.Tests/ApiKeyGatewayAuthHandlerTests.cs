using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class ApiKeyGatewayAuthHandlerTests
{
    [Fact]
    public async Task AuthenticateAsync_WithoutConfiguredKey_ReturnsDevelopmentIdentity()
    {
        var handler = new ApiKeyGatewayAuthHandler(apiKey: null, NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext());

        result.IsAuthenticated.ShouldBeTrue();
        result.Identity!.CallerId.ShouldBe("gateway-dev");
    }

    [Fact]
    public async Task AuthenticateAsync_WithMissingHeaders_ReturnsFailure()
    {
        var handler = new ApiKeyGatewayAuthHandler("secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext());

        result.IsAuthenticated.ShouldBeFalse();
        result.FailureReason.ShouldStartWith("Missing API key");
    }

    [Fact]
    public async Task AuthenticateAsync_WithInvalidApiKey_ReturnsFailure()
    {
        var handler = new ApiKeyGatewayAuthHandler("secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext(new Dictionary<string, string> { ["X-Api-Key"] = "wrong" }));

        result.IsAuthenticated.ShouldBeFalse();
        result.FailureReason.ShouldBe("Invalid API key.");
    }

    [Fact]
    public async Task AuthenticateAsync_WithBearerHeader_ReturnsSuccess()
    {
        var handler = new ApiKeyGatewayAuthHandler("secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext(new Dictionary<string, string> { ["Authorization"] = "Bearer secret" }));

        result.IsAuthenticated.ShouldBeTrue();
        result.Identity!.CallerId.ShouldBe("gateway-api-key");
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

        result.IsAuthenticated.ShouldBeTrue();
        result.Identity!.CallerId.ShouldBe("caller-a");
        result.Identity.TenantId.ShouldBe("tenant-a");
        result.Identity.Permissions.ShouldContain("chat:send");
        result.Identity.AllowedAgents.ShouldHaveSingleItem().ShouldBe("assistant-a");
    }

    [Fact]
    public async Task AuthenticateAsync_WithApiKeyHeader_ReturnsSuccess()
    {
        var handler = new ApiKeyGatewayAuthHandler("secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext(new Dictionary<string, string> { ["x-api-key"] = "secret" }));

        result.IsAuthenticated.ShouldBeTrue();
        result.Identity!.CallerId.ShouldBe("gateway-api-key");
    }

    [Fact]
    public async Task AuthenticateAsync_WithSatelliteApiKey_ReturnsSatelliteIdentity()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Satellites = new Dictionary<string, SatelliteConfig>
                {
                    ["sat-1"] = new()
                    {
                        Enabled = true,
                        ApiKey = "satellite-secret",
                        DisplayName = "Satellite One"
                    }
                }
            }
        };
        var handler = new ApiKeyGatewayAuthHandler(config, NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext(new Dictionary<string, string> { ["X-Api-Key"] = "satellite-secret" }));

        result.IsAuthenticated.ShouldBeTrue();
        result.Identity!.CallerId.ShouldBe("satellite:sat-1");
        result.Identity.IsAdmin.ShouldBeFalse();
        result.Identity.Permissions.ShouldContain("satellite:connect");
    }

    [Fact]
    public async Task AuthenticateAsync_WithMultipleKeys_ResolvesEachToCorrectIdentity()
    {
        var config = new PlatformConfig
        {
            ApiKey = "legacy-secret",
            Gateway = new GatewaySettingsConfig
            {
                ApiKeys = new Dictionary<string, ApiKeyConfig>
                {
                    ["tenant-a"] = new() { ApiKey = "key-a", TenantId = "tenant-a", CallerId = "caller-a" },
                    ["tenant-b"] = new() { ApiKey = "key-b", TenantId = "tenant-b", CallerId = "caller-b" }
                }
            }
        };
        var handler = new ApiKeyGatewayAuthHandler(config, NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var legacy = await handler.AuthenticateAsync(CreateContext(new Dictionary<string, string> { ["X-Api-Key"] = "legacy-secret" }));
        legacy.IsAuthenticated.ShouldBeTrue();
        legacy.Identity!.CallerId.ShouldBe("gateway-api-key");

        var a = await handler.AuthenticateAsync(CreateContext(new Dictionary<string, string> { ["X-Api-Key"] = "key-a" }));
        a.IsAuthenticated.ShouldBeTrue();
        a.Identity!.CallerId.ShouldBe("caller-a");

        var b = await handler.AuthenticateAsync(CreateContext(new Dictionary<string, string> { ["X-Api-Key"] = "key-b" }));
        b.IsAuthenticated.ShouldBeTrue();
        b.Identity!.CallerId.ShouldBe("caller-b");

        var bad = await handler.AuthenticateAsync(CreateContext(new Dictionary<string, string> { ["X-Api-Key"] = "key-c" }));
        bad.IsAuthenticated.ShouldBeFalse();
        bad.FailureReason.ShouldBe("Invalid API key.");
    }

    [Fact]
    public async Task AuthenticateAsync_WithPrefixOfValidKey_ReturnsFailure()
    {
        var handler = new ApiKeyGatewayAuthHandler("secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext(new Dictionary<string, string> { ["X-Api-Key"] = "secr" }));

        result.IsAuthenticated.ShouldBeFalse();
        result.FailureReason.ShouldBe("Invalid API key.");
    }

    [Fact]
    public async Task AuthenticateAsync_DevMode_WithoutOrigin_ReturnsDevelopmentIdentity()
    {
        // Non-browser clients (curl/CLI) send no Origin header and must still succeed.
        var handler = new ApiKeyGatewayAuthHandler(apiKey: null, NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(CreateContext());

        result.IsAuthenticated.ShouldBeTrue();
        result.Identity!.CallerId.ShouldBe("gateway-dev");
    }

    [Fact]
    public async Task AuthenticateAsync_DevMode_WithAllowedOrigin_ReturnsDevelopmentIdentity()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Cors = new CorsConfig { AllowedOrigins = ["http://localhost:5005"] }
            }
        };
        var handler = new ApiKeyGatewayAuthHandler(config, NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["Origin"] = "http://localhost:5005" }));

        result.IsAuthenticated.ShouldBeTrue();
        result.Identity!.CallerId.ShouldBe("gateway-dev");
    }

    [Fact]
    public async Task AuthenticateAsync_DevMode_WithDisallowedBrowserOrigin_ReturnsFailure()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Cors = new CorsConfig { AllowedOrigins = ["http://localhost:5005"] }
            }
        };
        var handler = new ApiKeyGatewayAuthHandler(config, NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["Origin"] = "http://evil.example.com" }));

        result.IsAuthenticated.ShouldBeFalse();
        result.FailureReason.ShouldStartWith("Origin not allowed");
    }

    [Fact]
    public async Task AuthenticateAsync_DevMode_WithDisallowedOrigin_DefaultAllowList_ReturnsFailure()
    {
        // No Cors config -> default allow-list is http://localhost:5005 only.
        var handler = new ApiKeyGatewayAuthHandler(apiKey: null, NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["Origin"] = "http://evil.example.com" }));

        result.IsAuthenticated.ShouldBeFalse();
        result.FailureReason.ShouldStartWith("Origin not allowed");
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

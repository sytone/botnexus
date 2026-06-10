using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotNexus.Gateway.Tests.Satellites;

public sealed class SatelliteAuthTests
{
    [Fact]
    public async Task SatelliteApiKey_AuthenticatesWithSatelliteIdentity()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Satellites = new Dictionary<string, SatelliteConfig>
                {
                    ["desktop"] = new()
                    {
                        DisplayName = "Jon Desktop",
                        ApiKey = "sat_test123",
                        Platform = "windows",
                        OwnerUserId = "jon",
                        Enabled = true
                    }
                }
            }
        };

        var handler = new ApiKeyGatewayAuthHandler(config, NullLogger<ApiKeyGatewayAuthHandler>.Instance);
        var context = new GatewayAuthContext
        {
            Headers = new Dictionary<string, string> { ["X-Api-Key"] = "sat_test123" },
            QueryParameters = new Dictionary<string, string>(),
            Path = "/api/satellites",
            Method = "GET"
        };

        var result = await handler.AuthenticateAsync(context);

        Assert.True(result.IsAuthenticated);
        Assert.NotNull(result.Identity);
        Assert.Equal("satellite:desktop", result.Identity.CallerId);
        Assert.Equal("Jon Desktop", result.Identity.DisplayName);
        Assert.False(result.Identity.IsAdmin);
        Assert.Contains("satellite:connect", result.Identity.Permissions);
        Assert.Contains("satellite:heartbeat", result.Identity.Permissions);
    }

    [Fact]
    public async Task SatelliteApiKey_DisabledSatellite_Rejected()
    {
        var config = new PlatformConfig
        {
            ApiKey = "admin_key",
            Gateway = new GatewaySettingsConfig
            {
                Satellites = new Dictionary<string, SatelliteConfig>
                {
                    ["disabled"] = new()
                    {
                        DisplayName = "Disabled",
                        ApiKey = "sat_disabled",
                        Platform = "windows",
                        OwnerUserId = "jon",
                        Enabled = false
                    }
                }
            }
        };

        var handler = new ApiKeyGatewayAuthHandler(config, NullLogger<ApiKeyGatewayAuthHandler>.Instance);
        var context = new GatewayAuthContext
        {
            Headers = new Dictionary<string, string> { ["X-Api-Key"] = "sat_disabled" },
            QueryParameters = new Dictionary<string, string>(),
            Path = "/api/satellites",
            Method = "GET"
        };

        var result = await handler.AuthenticateAsync(context);
        Assert.False(result.IsAuthenticated);
    }

    [Fact]
    public async Task SatelliteApiKey_NoApiKeyConfigured_Rejected()
    {
        var config = new PlatformConfig
        {
            ApiKey = "admin_key",
            Gateway = new GatewaySettingsConfig
            {
                Satellites = new Dictionary<string, SatelliteConfig>
                {
                    ["nokey"] = new()
                    {
                        DisplayName = "No Key",
                        Platform = "windows",
                        OwnerUserId = "jon",
                        Enabled = true
                        // ApiKey intentionally null
                    }
                }
            }
        };

        var handler = new ApiKeyGatewayAuthHandler(config, NullLogger<ApiKeyGatewayAuthHandler>.Instance);
        var context = new GatewayAuthContext
        {
            Headers = new Dictionary<string, string> { ["X-Api-Key"] = "anything" },
            QueryParameters = new Dictionary<string, string>(),
            Path = "/api/satellites",
            Method = "GET"
        };

        var result = await handler.AuthenticateAsync(context);
        Assert.False(result.IsAuthenticated);
    }

    [Fact]
    public async Task SatelliteAndApiKeys_BothWork()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                ApiKeys = new Dictionary<string, ApiKeyConfig>
                {
                    ["admin"] = new() { ApiKey = "admin_key", CallerId = "admin-caller", IsAdmin = true }
                },
                Satellites = new Dictionary<string, SatelliteConfig>
                {
                    ["sat1"] = new()
                    {
                        DisplayName = "Satellite",
                        ApiKey = "sat_key",
                        Platform = "windows",
                        OwnerUserId = "jon",
                        Enabled = true
                    }
                }
            }
        };

        var handler = new ApiKeyGatewayAuthHandler(config, NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        // Admin key works
        var adminResult = await handler.AuthenticateAsync(new GatewayAuthContext
        {
            Headers = new Dictionary<string, string> { ["X-Api-Key"] = "admin_key" },
            QueryParameters = new Dictionary<string, string>(),
            Path = "/api/agents",
            Method = "GET"
        });
        Assert.True(adminResult.IsAuthenticated);
        Assert.Equal("admin-caller", adminResult.Identity!.CallerId);

        // Satellite key works
        var satResult = await handler.AuthenticateAsync(new GatewayAuthContext
        {
            Headers = new Dictionary<string, string> { ["X-Api-Key"] = "sat_key" },
            QueryParameters = new Dictionary<string, string>(),
            Path = "/api/satellites",
            Method = "GET"
        });
        Assert.True(satResult.IsAuthenticated);
        Assert.Equal("satellite:sat1", satResult.Identity!.CallerId);
    }
}

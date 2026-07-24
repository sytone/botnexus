using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.FeatureManagement;

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
    public async Task AuthenticateAsync_InvalidReloadedConfig_UsesLastKnownAuthSnapshot()
    {
        var initial = new PlatformConfig { ApiKey = "secret" };
        var monitor = new ThrowingOptionsMonitor(initial);
        var handler = new ApiKeyGatewayAuthHandler(monitor, NullLogger<ApiKeyGatewayAuthHandler>.Instance);
        monitor.ThrowOnRead = true;

        var result = await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["X-Api-Key"] = "secret" }));

        result.IsAuthenticated.ShouldBeTrue();
        result.Identity!.CallerId.ShouldBe("gateway-api-key");
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
    public async Task AuthenticateAsync_DevMode_FlagOff_DisallowedOrigin_StillSucceeds()
    {
        // #1931 rollout safety: with the GatewayDevOriginEnforcement flag OFF (the default,
        // and what a null feature manager represents), even a disallowed browser Origin must
        // NOT be rejected - introducing the guard can never lock a keyless user out on restart.
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Cors = new CorsConfig { AllowedOrigins = ["http://localhost:5005"] }
            }
        };
        var handler = new ApiKeyGatewayAuthHandler(
            config,
            NullLogger<ApiKeyGatewayAuthHandler>.Instance,
            securityEvents: null,
            featureManager: new StubFeatureManager(enabled: false));

        var result = await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["Origin"] = "http://evil.example.com" }));

        result.IsAuthenticated.ShouldBeTrue();
        result.Identity!.CallerId.ShouldBe("gateway-dev");
    }

    [Fact]
    public async Task AuthenticateAsync_DevMode_NullFeatureManager_DisallowedOrigin_StillSucceeds()
    {
        // A handler constructed without a feature manager (e.g. non-DI paths) treats the guard
        // as disabled - same fail-open default as flag-off.
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

        result.IsAuthenticated.ShouldBeTrue();
        result.Identity!.CallerId.ShouldBe("gateway-dev");
    }

    [Fact]
    public async Task AuthenticateAsync_DevMode_FlagOn_WithoutOrigin_ReturnsDevelopmentIdentity()
    {
        // Even with the guard ON, non-browser clients (no Origin) must still succeed.
        var handler = new ApiKeyGatewayAuthHandler(
            apiKey: null,
            NullLogger<ApiKeyGatewayAuthHandler>.Instance,
            securityEvents: null,
            featureManager: new StubFeatureManager(enabled: true));

        var result = await handler.AuthenticateAsync(CreateContext());

        result.IsAuthenticated.ShouldBeTrue();
        result.Identity!.CallerId.ShouldBe("gateway-dev");
    }

    [Fact]
    public async Task AuthenticateAsync_DevMode_FlagOn_WithAllowedOrigin_ReturnsDevelopmentIdentity()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Cors = new CorsConfig { AllowedOrigins = ["http://localhost:5005"] }
            }
        };
        var handler = new ApiKeyGatewayAuthHandler(
            config,
            NullLogger<ApiKeyGatewayAuthHandler>.Instance,
            securityEvents: null,
            featureManager: new StubFeatureManager(enabled: true));

        var result = await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["Origin"] = "http://localhost:5005" }));

        result.IsAuthenticated.ShouldBeTrue();
        result.Identity!.CallerId.ShouldBe("gateway-dev");
    }

    [Fact]
    public async Task AuthenticateAsync_DevMode_FlagOn_WithDisallowedBrowserOrigin_ReturnsFailure()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Cors = new CorsConfig { AllowedOrigins = ["http://localhost:5005"] }
            }
        };
        var handler = new ApiKeyGatewayAuthHandler(
            config,
            NullLogger<ApiKeyGatewayAuthHandler>.Instance,
            securityEvents: null,
            featureManager: new StubFeatureManager(enabled: true));

        var result = await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["Origin"] = "http://evil.example.com" }));

        result.IsAuthenticated.ShouldBeFalse();
        result.FailureReason.ShouldStartWith("Origin not allowed");
    }

    [Fact]
    public async Task AuthenticateAsync_DevMode_FlagOn_DisallowedOrigin_DefaultAllowList_ReturnsFailure()
    {
        // No Cors config -> default allow-list is http://localhost:5005 only.
        var handler = new ApiKeyGatewayAuthHandler(
            apiKey: null,
            NullLogger<ApiKeyGatewayAuthHandler>.Instance,
            securityEvents: null,
            featureManager: new StubFeatureManager(enabled: true));

        var result = await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["Origin"] = "http://evil.example.com" }));

        result.IsAuthenticated.ShouldBeFalse();
        result.FailureReason.ShouldStartWith("Origin not allowed");
    }

    [Fact]
    public async Task AuthenticateAsync_DevMode_FlagUnspecified_DisallowedBrowserOrigin_ReturnsFailure()
    {
        // #1946: the dev-origin guard is now ON by default. When the operator has not specified
        // the GatewayDevOriginEnforcement flag at all, a disallowed browser Origin must be rejected
        // out-of-the-box for keyless gateways.
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Cors = new CorsConfig { AllowedOrigins = ["http://localhost:5005"] }
            }
        };
        using var provider = BuildFeatureServices(configuredValue: null);
        var handler = new ApiKeyGatewayAuthHandler(
            config,
            NullLogger<ApiKeyGatewayAuthHandler>.Instance,
            securityEvents: null,
            featureManager: provider.GetRequiredService<IFeatureManager>(),
            configuration: provider.GetRequiredService<IConfiguration>());

        var result = await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["Origin"] = "http://evil.example.com" }));

        result.IsAuthenticated.ShouldBeFalse();
        result.FailureReason.ShouldStartWith("Origin not allowed");
    }

    [Fact]
    public async Task AuthenticateAsync_DevMode_FlagUnspecified_WithoutOrigin_ReturnsDevelopmentIdentity()
    {
        // Default-ON guard must still allow non-browser clients (no Origin header).
        using var provider = BuildFeatureServices(configuredValue: null);
        var handler = new ApiKeyGatewayAuthHandler(
            apiKey: null,
            NullLogger<ApiKeyGatewayAuthHandler>.Instance,
            securityEvents: null,
            featureManager: provider.GetRequiredService<IFeatureManager>(),
            configuration: provider.GetRequiredService<IConfiguration>());

        var result = await handler.AuthenticateAsync(CreateContext());

        result.IsAuthenticated.ShouldBeTrue();
        result.Identity!.CallerId.ShouldBe("gateway-dev");
    }

    [Fact]
    public async Task AuthenticateAsync_DevMode_FlagExplicitlyFalse_DisallowedOrigin_StillSucceeds()
    {
        // Operators must retain the escape hatch: FeatureManagement.GatewayDevOriginEnforcement: false
        // explicitly disables the guard even though the new default is ON.
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Cors = new CorsConfig { AllowedOrigins = ["http://localhost:5005"] }
            }
        };
        using var provider = BuildFeatureServices(configuredValue: false);
        var handler = new ApiKeyGatewayAuthHandler(
            config,
            NullLogger<ApiKeyGatewayAuthHandler>.Instance,
            securityEvents: null,
            featureManager: provider.GetRequiredService<IFeatureManager>(),
            configuration: provider.GetRequiredService<IConfiguration>());

        var result = await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["Origin"] = "http://evil.example.com" }));

        result.IsAuthenticated.ShouldBeTrue();
        result.Identity!.CallerId.ShouldBe("gateway-dev");
    }

    [Fact]
    public async Task AuthenticateAsync_DevMode_FlagEvaluationThrows_FailsOpen_DisallowedOrigin_StillSucceeds()
    {
        // Fail-open contract: a feature-flag evaluation fault must never lock the operator out.
        // Even with a disallowed Origin and the new default-ON policy, an evaluation error leaves
        // the guard inert.
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Cors = new CorsConfig { AllowedOrigins = ["http://localhost:5005"] }
            }
        };
        using var provider = BuildFeatureServices(configuredValue: null);
        var handler = new ApiKeyGatewayAuthHandler(
            config,
            NullLogger<ApiKeyGatewayAuthHandler>.Instance,
            securityEvents: null,
            featureManager: new ThrowingFeatureManager(),
            configuration: provider.GetRequiredService<IConfiguration>());

        var result = await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["Origin"] = "http://evil.example.com" }));

        result.IsAuthenticated.ShouldBeTrue();
        result.Identity!.CallerId.ShouldBe("gateway-dev");
    }

    // Builds a real Microsoft.FeatureManagement pipeline over an in-memory configuration so the
    // "unspecified vs explicit false" distinction is exercised through the production code path.
    private static ServiceProvider BuildFeatureServices(bool? configuredValue)
    {
        var settings = new Dictionary<string, string?>();
        if (configuredValue is { } value)
            settings["FeatureManagement:GatewayDevOriginEnforcement"] = value ? "true" : "false";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddFeatureManagement();
        return services.BuildServiceProvider();
    }

    private sealed class ThrowingFeatureManager : IFeatureManager
    {
        public async IAsyncEnumerable<string> GetFeatureNamesAsync()
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> IsEnabledAsync(string feature) =>
            throw new InvalidOperationException("feature evaluation failed");

        public Task<bool> IsEnabledAsync<TContext>(string feature, TContext context) =>
            throw new InvalidOperationException("feature evaluation failed");
    }

    private static GatewayAuthContext CreateContext(IReadOnlyDictionary<string, string>? headers = null)
        => new()
        {
            Headers = headers ?? new Dictionary<string, string>(),
            QueryParameters = new Dictionary<string, string>(),
            Path = "/api/messages",
            Method = "POST"
        };

    private sealed class ThrowingOptionsMonitor(PlatformConfig initial) : IOptionsMonitor<PlatformConfig>
    {
        public bool ThrowOnRead { get; set; }

        public PlatformConfig CurrentValue => ThrowOnRead
            ? throw new OptionsValidationException(nameof(PlatformConfig), typeof(PlatformConfig), ["invalid reload"])
            : initial;

        public PlatformConfig Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<PlatformConfig, string?> listener) => null;
    }

    /// <summary>
    /// Minimal <see cref="IFeatureManager"/> stub returning a fixed enabled/disabled result for
    /// every flag, so the dev-mode Origin guard can be exercised in both states without a full
    /// configuration-backed feature manager.
    /// </summary>
    private sealed class StubFeatureManager(bool enabled) : IFeatureManager
    {
        public async IAsyncEnumerable<string> GetFeatureNamesAsync()
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> IsEnabledAsync(string feature) => Task.FromResult(enabled);

        public Task<bool> IsEnabledAsync<TContext>(string feature, TContext context) => Task.FromResult(enabled);
    }
}

using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Fast, in-process unit tests for <see cref="ConfigPathResolver"/> that exercise the token
/// resolution and create-missing side-effect paths directly (no CLI subprocess). These pin the
/// behaviour of the <c>ResolveMember</c> / <c>ResolveIndex</c> split introduced for #1393 so the
/// side-effecting intermediate-instantiation paths stay testable in isolation.
/// </summary>
public sealed class ConfigPathResolverUnitTests
{
    private readonly ConfigPathResolver _resolver = new();

    [Fact]
    public void TryGetValue_ResolvesNestedPropertyPath()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig { ListenUrl = "http://localhost:5005" }
        };

        var ok = _resolver.TryGetValue(config, "gateway.listenUrl", out var value, out var error);

        ok.ShouldBeTrue(error);
        value.ShouldBe("http://localhost:5005");
    }

    [Fact]
    public void TryGetValue_ResolvesDictionaryKeyCaseInsensitively()
    {
        var config = new PlatformConfig
        {
            Providers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Copilot"] = new ProviderConfig { ApiKey = "abc123" }
            }
        };

        var ok = _resolver.TryGetValue(config, "PROVIDERS.copilot.apiKey", out var value, out var error);

        ok.ShouldBeTrue(error);
        value.ShouldBe("abc123");
    }

    [Fact]
    public void TryGetValue_MissingProperty_ReturnsDescriptiveError()
    {
        var config = new PlatformConfig { Gateway = new GatewaySettingsConfig() };

        var ok = _resolver.TryGetValue(config, "gateway.doesNotExist", out _, out var error);

        ok.ShouldBeFalse();
        error.ShouldContain("Property 'doesNotExist' does not exist");
    }

    [Fact]
    public void TrySetValue_CreatesMissingIntermediateObject_ForTerminalScalar()
    {
        // gateway.rateLimit is null; setting the scalar `enabled` must auto-create the
        // RateLimitConfig intermediate and assign the bool rather than try to instantiate the
        // leaf bool type (regression #598). This is the ResolveMember create-missing path.
        var config = new PlatformConfig { Gateway = new GatewaySettingsConfig() };

        var ok = _resolver.TrySetValue(config, "gateway.rateLimit.enabled", "true", out var error);

        ok.ShouldBeTrue(error);
        config.Gateway!.RateLimit.ShouldNotBeNull();
        config.Gateway.RateLimit!.Enabled.ShouldBeTrue();
    }

    [Fact]
    public void TrySetValue_CreatesMissingDictionaryEntry_WhenSettingObjectValue()
    {
        // providers dictionary exists but has no "copilot" key; setting it must create the entry.
        var config = new PlatformConfig { Providers = new(StringComparer.OrdinalIgnoreCase) };

        var ok = _resolver.TrySetValue(config, "providers.copilot", "{\"apiKey\":\"token-1\"}", out var error);

        ok.ShouldBeTrue(error);
        config.Providers!.ShouldContainKey("copilot");
        config.Providers["copilot"].ApiKey.ShouldBe("token-1");
    }

    [Fact]
    public void TrySetValue_UpdatesExistingListElementByIndex()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Cors = new CorsConfig { AllowedOrigins = new() { "https://one.test", "https://two.test" } }
            }
        };

        var ok = _resolver.TrySetValue(config, "gateway.cors.allowedOrigins[0]", "https://updated.test", out var error);

        ok.ShouldBeTrue(error);
        config.Gateway!.Cors!.AllowedOrigins![0].ShouldBe("https://updated.test");
        config.Gateway.Cors.AllowedOrigins[1].ShouldBe("https://two.test");
    }

    [Fact]
    public void TrySetValue_DeserializesJsonArrayIntoMissingListProperty()
    {
        var config = new PlatformConfig { Gateway = new GatewaySettingsConfig() };

        var ok = _resolver.TrySetValue(
            config,
            "gateway.cors.allowedOrigins",
            "[\"https://one.test\",\"https://two.test\"]",
            out var error);

        ok.ShouldBeTrue(error);
        config.Gateway!.Cors!.AllowedOrigins.ShouldBe(new[] { "https://one.test", "https://two.test" });
    }

    [Fact]
    public void TryGetValue_IndexOutOfRange_ReturnsError_AndDoesNotMutate()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Cors = new CorsConfig { AllowedOrigins = new() { "https://one.test" } }
            }
        };

        var ok = _resolver.TryGetValue(config, "gateway.cors.allowedOrigins[5]", out _, out var error);

        ok.ShouldBeFalse();
        error.ShouldContain("out of range");
        // read path must not grow the list
        config.Gateway!.Cors!.AllowedOrigins!.Count.ShouldBe(1);
    }

    [Fact]
    public void TrySetValue_WritesNull_ToNullableProperty()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig { DefaultAgentId = "assistant" }
        };

        var ok = _resolver.TrySetValue(config, "gateway.defaultAgentId", "null", out var error);

        ok.ShouldBeTrue(error);
        config.Gateway!.DefaultAgentId.ShouldBeNull();
    }

    [Fact]
    public void TrySetValue_InvalidBoolean_ReturnsConversionError()
    {
        var config = new PlatformConfig { Gateway = new GatewaySettingsConfig() };

        var ok = _resolver.TrySetValue(config, "gateway.rateLimit.enabled", "not-a-bool", out var error);

        ok.ShouldBeFalse();
        error.ShouldContain("is not a valid boolean");
    }
}

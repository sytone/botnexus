using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Pure, in-memory unit tests for <see cref="PlatformConfigValidator"/>. These construct a
/// <see cref="PlatformConfig"/> graph directly (no config-file fixtures, no filesystem) and assert
/// against the validator's return value. They exist to prove the testability win from extracting the
/// validation engine out of <see cref="PlatformConfigLoader"/> (#1764): the rules are exercisable as a
/// plain <c>(PlatformConfig) -&gt; IReadOnlyList&lt;string&gt;</c> function with zero I/O, and the
/// loader's forwarding shims stay behaviourally identical to the extracted implementation.
/// </summary>
public sealed class PlatformConfigValidatorTests
{
    [Theory]
    [InlineData("coder")]
    [InlineData("reviewer")]
    [InlineData("researcher")]
    public void Validate_ReservedArchetypeAgentId_ReturnsError(string archetypeId)
    {
        // #2136: reserved worker-archetype ids cannot be defined as named agents in config.
        var config = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["copilot"] = new() { ApiKey = "test-key" }
            },
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                [archetypeId] = new() { Provider = "copilot", Model = "gpt-4.1" }
            }
        };

        var errors = PlatformConfigValidator.Validate(config);

        errors.ShouldContain(e => e.Contains(archetypeId) && e.Contains("reserved"));
    }

    [Fact]
    public void Validate_MinimalValidInMemoryConfig_NoErrors()
    {
        var config = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["copilot"] = new() { ApiKey = "test-key" }
            },
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new() { Provider = "copilot", Model = "gpt-4.1" }
            }
        };

        PlatformConfigValidator.Validate(config).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_InvalidGatewayValues_ReturnsCrossFieldErrors()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                ListenUrl = "not-a-url",
                LogLevel = "verbose",
                AgentsDirectory = "bad\0path"
            }
        };

        var errors = PlatformConfigValidator.Validate(config);

        errors.ShouldContain(e => e.Contains("listenUrl", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("logLevel", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("agentsDirectory", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ProviderWithNonHttpBaseUrl_ReturnsBaseUrlError()
    {
        var config = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["copilot"] = new() { ApiKey = "test", BaseUrl = "ftp://invalid-endpoint" }
            }
        };

        PlatformConfigValidator.Validate(config)
            .Where(e => e.Contains("providers.copilot.baseUrl", StringComparison.Ordinal))
            .ShouldHaveSingleItem();
    }

    [Fact]
    public void Validate_AgentWithInvalidMemoryPromptInjection_ReturnsPromptInjectionError()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new()
                {
                    Provider = "copilot",
                    Model = "gpt-4.1",
                    Memory = new MemoryAgentConfig { PromptInjection = "partial" }
                }
            }
        };

        PlatformConfigValidator.Validate(config)
            .ShouldContain(e => e.Contains("agents.assistant.memory.promptInjection", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateWarnings_NewerVersion_ReturnsBestEffortWarning()
    {
        var warnings = PlatformConfigValidator.ValidateWarnings(new PlatformConfig { PlatformVersion = 2 });

        warnings.Where(w => w.Contains("version '2'", StringComparison.Ordinal)).ShouldHaveSingleItem();
    }

    [Fact]
    public void ValidateWarnings_KnownVersion_ReturnsNoWarnings()
        => PlatformConfigValidator.ValidateWarnings(new PlatformConfig { PlatformVersion = 1 }).ShouldBeEmpty();

    [Fact]
    public void ValidateAnnotated_ValidInMemoryConfig_NoErrors()
    {
        var config = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["copilot"] = new() { ApiKey = "test-key" }
            },
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new() { Provider = "copilot", Model = "gpt-4.1" }
            }
        };

        PlatformConfigValidator.ValidateAnnotated(config).ShouldBeEmpty();
    }

    [Fact]
    public void CollectCrossFieldErrors_InvalidListenUrl_ReturnsListenUrlError()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig { ListenUrl = "ws://localhost:8080" }
        };

        PlatformConfigValidator.CollectCrossFieldErrors(config)
            .Where(e => e.Contains("listenUrl", StringComparison.Ordinal))
            .ShouldHaveSingleItem();
    }

    [Fact]
    public void Validate_NullConfig_Throws()
        => Should.Throw<ArgumentNullException>(() => PlatformConfigValidator.Validate(null!));

    [Fact]
    public void LoaderShim_DelegatesIdenticallyToValidator()
    {
        // The loader keeps one-line forwarding shims (#1764). Assert the shim and the extracted
        // implementation produce byte-identical results for the same config so no caller sees a
        // behaviour change from the extraction.
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                ListenUrl = "not-a-url",
                LogLevel = "verbose"
            }
        };

        var viaShim = PlatformConfigLoader.Validate(config);
        var viaValidator = PlatformConfigValidator.Validate(config);

        viaShim.ShouldBe(viaValidator);
    }

    [Fact]
    public void LoaderWarningsShim_DelegatesIdenticallyToValidator()
    {
        var config = new PlatformConfig { PlatformVersion = 3 };

        PlatformConfigLoader.ValidateWarnings(config)
            .ShouldBe(PlatformConfigValidator.ValidateWarnings(config));
    }
}

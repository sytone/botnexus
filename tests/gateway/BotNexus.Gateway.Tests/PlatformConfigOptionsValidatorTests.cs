using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests;

public sealed class PlatformConfigOptionsValidatorTests
{
    [Fact]
    public void Validate_WithAgentScopedFailure_DoesNotPoisonGlobalOptions()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["invalid"] = new()
                {
                    Provider = "copilot",
                    Model = "gpt-4.1",
                    Thinking = "warp-speed"
                }
            }
        };

        var result = new PlatformConfigOptionsValidator().Validate(null, config);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validate_WithGatewayScopedFailure_StillFailsGlobalOptions()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig { LogLevel = "definitely-not-a-level" }
        };

        var result = new PlatformConfigOptionsValidator().Validate(null, config);

        result.Failed.ShouldBeTrue();
    }
}

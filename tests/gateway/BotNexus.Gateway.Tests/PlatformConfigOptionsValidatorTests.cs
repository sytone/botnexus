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

    [Fact]
    public void Validate_WithAgentScopedCrossFieldFailure_DoesNotPoisonGlobalOptions()
    {
        // #2102: a per-agent cross-field error (here: missing provider) must be quarantined so it
        // never fails GLOBAL options. The invalid descriptor is skipped at config load
        // (PlatformConfigAgentSource), so failing global options here would block unrelated tools
        // for every agent - the exact denial loop #2102 removes.
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["coder"] = new()
                {
                    // Provider intentionally omitted -> "agents.coder.provider is required".
                    Model = "gpt-4.1"
                }
            }
        };

        var result = new PlatformConfigOptionsValidator().Validate(null, config);

        result.Succeeded.ShouldBeTrue(
            "A per-agent cross-field descriptor error must be quarantined, not fail global options: " +
            string.Join("; ", result.Failures ?? []));
    }

    [Fact]
    public void Validate_WithInvalidGatewayAndInvalidAgent_FailsOnlyForGateway()
    {
        // Gateway-scoped errors still fail hard; the agent-scoped errors are quarantined and must
        // not themselves appear in the failures.
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig { LogLevel = "definitely-not-a-level" },
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["coder"] = new()
                {
                    Model = "gpt-4.1",
                    Thinking = "warp-speed"
                }
            }
        };

        var result = new PlatformConfigOptionsValidator().Validate(null, config);

        result.Failed.ShouldBeTrue();
        (result.Failures ?? []).ShouldNotContain(
            failure => failure.Contains("agents.coder", StringComparison.OrdinalIgnoreCase),
            "Agent-scoped errors must be quarantined, not surfaced as global option failures.");
    }

    [Fact]
    public void Validate_WithInvalidAgentsDefaults_StillFailsGlobalOptions()
    {
        // The reserved 'defaults' pseudo-agent is not a real descriptor that can be skipped; its
        // errors seed every agent and must remain a hard global failure (not quarantined).
        var config = new PlatformConfig
        {
            AgentDefaults = new AgentDefaultsConfig
            {
                Heartbeat = new BotNexus.Gateway.Abstractions.Models.HeartbeatAgentConfig
                {
                    IntervalMinutes = 0
                }
            }
        };

        var result = new PlatformConfigOptionsValidator().Validate(null, config);

        result.Failed.ShouldBeTrue();
    }
}

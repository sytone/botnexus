using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class ExtensionConfigScopeReaderTests
{
    private sealed class ScopeConfig
    {
        public string? Scope { get; set; }
    }

    [Fact]
    public void BindOperations_ReadExactlyOneConcreteScope()
    {
        var config = new PlatformConfig
        {
            World = new WorldSettingsConfig { Extensions = Bag("world") },
            Gateway = new GatewaySettingsConfig { Extensions = Bag("gateway") },
            AgentDefaults = new AgentDefaultsConfig { Extensions = Bag("default") },
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new() { Extensions = Bag("agent") }
            }
        };

        (config.BindWorldExtension<ScopeConfig>("sample") ?? throw new InvalidOperationException()).Scope.ShouldBe("world");
        (config.BindGatewayExtension<ScopeConfig>("sample") ?? throw new InvalidOperationException()).Scope.ShouldBe("gateway");
        (config.BindAgentDefaultExtension<ScopeConfig>("sample") ?? throw new InvalidOperationException()).Scope.ShouldBe("default");
        (config.BindAgentExtension<ScopeConfig>(AgentId.From("assistant"), "sample") ?? throw new InvalidOperationException()).Scope.ShouldBe("agent");
        config.BindAgentExtension<ScopeConfig>(AgentId.From("missing"), "sample").ShouldBeNull();
    }

    private static Dictionary<string, JsonElement> Bag(string scope)
        => new() { ["sample"] = JsonSerializer.SerializeToElement(new { scope }) };
}

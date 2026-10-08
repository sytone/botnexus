using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>The operator surface carries names; resolving ambient values is exclusively a launch concern.</summary>
public sealed class LocalChildEnvironmentProjectionTests
{
    [Fact]
    public void ConfigAndSchema_ExposeOnlyNamesNotAmbientValues()
    {
        const string name = "BN4749_PROJECTION_TOKEN";
        const string value = "synthetic-projection-secret";
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig { LocalChildEnvironmentPassThrough = [name] }
        };
        var policy = new LocalChildEnvironmentPolicy(config.Gateway.LocalChildEnvironmentPassThrough);
        var target = new Dictionary<string, string?>();
        LocalChildEnvironment.Populate(target, new Dictionary<string, string?> { [name] = value }, policy);
        target[name].ShouldBe(value);
        var serialized = JsonSerializer.Serialize(config);
        serialized.ShouldContain(name);
        serialized.ShouldNotContain(value);
        var schema = PlatformConfigSchema.GenerateSchemaJson();
        schema.ShouldContain("localChildEnvironmentPassThrough");
        schema.ShouldNotContain(value);
        schema.ShouldNotContain(name);
    }
}

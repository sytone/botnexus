using System.Text.Json;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Configuration.Tests;

public sealed class StrictOpaqueConfigMutationTests
{
    [Theory]
    [InlineData("{\"parent\":false}", "parent.child")]
    [InlineData("{\"items\":[]}", "items[1].name")]
    [InlineData("{\"items\":[false]}", "items[0].name")]
    [InlineData("{\"items\":false}", "items[0]")]
    public void TrySetFrom_LatestShapeDiffersFromPreflight_RejectsAtomically(string latest, string suffix)
    {
        var resolver = new ConfigPathResolver();
        var preflight = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Extensions = new() { ["sample"] = JsonSerializer.Deserialize<JsonElement>(
                    """{"parent":{},"items":[{"name":"a"},{"name":"b"}]}""") }
            }
        };
        var path = "gateway.extensions.sample." + suffix;
        resolver.TrySetValue(preflight, path, "changed", out var error).ShouldBeTrue(error);
        resolver.TryGetValue(preflight, path, out var coerced, out error).ShouldBeTrue(error);
        // The writer's callback sees a fresh latest document, not the preflight graph.
        var document = ConfigDocument.Parse("{\"gateway\":{\"extensions\":{\"sample\":" + latest + "}}}");
        var before = document.ToJsonString();
        document.TrySetFrom(path, coerced, out error).ShouldBeFalse();
        error.ShouldContain(path);
        document.ToJsonString().ShouldBe(before);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TrySetValue_FailedOpaqueEdit_DoesNotAttachParents(int scenario)
    {
        var config = new PlatformConfig { Gateway = null, AgentDefaults = null };
        var path = "gateway.extensions.new.child[2]";
        if (scenario == 1)
            config.Gateway = new GatewaySettingsConfig { Extensions = null };
        if (scenario == 2)
            config.Gateway = new GatewaySettingsConfig { Extensions = new() };
        if (scenario >= 3)
        {
            path = "agents.defaults.extensions.new.child[2]";
            if (scenario == 4)
                config.AgentDefaults = new AgentDefaultsConfig { Extensions = null };
        }
        var gateway = config.Gateway;
        var extensions = gateway?.Extensions;
        var defaults = config.AgentDefaults;
        var resolver = new ConfigPathResolver();
        resolver.TrySetValue(config, path, "changed", out var error).ShouldBeFalse();
        error.ShouldContain(path);
        // AgentDefaults is JsonIgnore; serialized equality cannot prove this invariant.
        ReferenceEquals(config.Gateway, gateway).ShouldBeTrue();
        ReferenceEquals(config.Gateway?.Extensions, extensions).ShouldBeTrue();
        ReferenceEquals(config.AgentDefaults, defaults).ShouldBeTrue();
        config.AgentDefaults?.Extensions.ShouldBeNull();
        extensions?.Count.ShouldBe(0);
    }

    [Theory]
    [InlineData("{\"parent\":null}", "parent.child")]
    [InlineData("{\"items\":null}", "items[0].name")]
    [InlineData("{\"items\":[null]}", "items[0].name")]
    public void NullJsonParent_IsMaterializedConsistently(string json, string suffix)
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Extensions = new() { ["sample"] = JsonSerializer.Deserialize<JsonElement>(json) }
            }
        };
        var path = "gateway.extensions.sample." + suffix;
        var resolver = new ConfigPathResolver();
        resolver.TrySetValue(config, path, "true", out var error).ShouldBeTrue(error);
        resolver.TryGetValue(config, path, out var coerced, out error).ShouldBeTrue(error);
        var document = ConfigDocument.Parse("{\"gateway\":{\"extensions\":{\"sample\":" + json + "}}}");
        document.TrySetFrom(path, coerced, out error).ShouldBeTrue(error);
        document.GetBool(path).ShouldBe(true);
    }

    [Fact]
    public void RootJsonArray_IsRecognizedAndMutated()
    {
        const string path = "gateway.extensions.sample[0].name";
        ConfigPathBinding.TryRecognise(path, out var error).ShouldBeTrue(error);
        var document = ConfigDocument.Parse("""{"gateway":{"extensions":{"sample":[{"name":"old","keep":17}]}}}""");
        document.TrySetFrom(path, "new", out error).ShouldBeTrue(error);
        document.TryGetString(path, out var value).ShouldBeTrue();
        value.ShouldBe("new");
        document.GetInt("gateway.extensions.sample[0].keep").ShouldBe(17);
    }
}

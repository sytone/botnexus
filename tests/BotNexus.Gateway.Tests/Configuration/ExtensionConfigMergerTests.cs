using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using FluentAssertions;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class ExtensionConfigMergerTests
{
    [Fact]
    public void Merge_BothNull_ReturnsEmpty()
    {
        var merged = ExtensionConfigMerger.Merge(null, null);

        merged.Should().BeEmpty();
    }

    [Fact]
    public void Merge_WorldOnly_ReturnsWorldDefaults()
    {
        var worldDefaults = BuildConfig("ext", """{"a":1}""");

        var merged = ExtensionConfigMerger.Merge(worldDefaults, null);

        AssertJsonEquals(merged["ext"], """{"a":1}""");
    }

    [Fact]
    public void Merge_AgentOnly_ReturnsAgentConfig()
    {
        var agentOverrides = BuildConfig("ext", """{"b":2}""");

        var merged = ExtensionConfigMerger.Merge(null, agentOverrides);

        AssertJsonEquals(merged["ext"], """{"b":2}""");
    }

    [Fact]
    public void Merge_BothPresent_DeepMergesObjects()
    {
        var worldDefaults = BuildConfig("ext", """{"a":1,"b":2}""");
        var agentOverrides = BuildConfig("ext", """{"b":3,"c":4}""");

        var merged = ExtensionConfigMerger.Merge(worldDefaults, agentOverrides);

        AssertJsonEquals(merged["ext"], """{"a":1,"b":3,"c":4}""");
    }

    [Fact]
    public void Merge_NestedObjects_MergesRecursively()
    {
        var worldDefaults = BuildConfig("ext", """{"nested":{"x":1}}""");
        var agentOverrides = BuildConfig("ext", """{"nested":{"y":2}}""");

        var merged = ExtensionConfigMerger.Merge(worldDefaults, agentOverrides);

        AssertJsonEquals(merged["ext"], """{"nested":{"x":1,"y":2}}""");
    }

    [Fact]
    public void Merge_ScalarOverride_AgentWins()
    {
        var worldDefaults = BuildConfig("ext", """{"val":"world"}""");
        var agentOverrides = BuildConfig("ext", """{"val":"agent"}""");

        var merged = ExtensionConfigMerger.Merge(worldDefaults, agentOverrides);

        AssertJsonEquals(merged["ext"], """{"val":"agent"}""");
    }

    [Fact]
    public void Merge_ArrayReplace_AgentWins()
    {
        var worldDefaults = BuildConfig("ext", """{"list":[1,2]}""");
        var agentOverrides = BuildConfig("ext", """{"list":[3]}""");

        var merged = ExtensionConfigMerger.Merge(worldDefaults, agentOverrides);

        AssertJsonEquals(merged["ext"], """{"list":[3]}""");
    }

    [Fact]
    public void Merge_AgentDisablesExtension_ExplicitFalse()
    {
        var worldDefaults = BuildConfig("ext", """{"enabled":true,"x":1}""");
        var agentOverrides = BuildConfig("ext", """{"enabled":false}""");

        var merged = ExtensionConfigMerger.Merge(worldDefaults, agentOverrides);

        AssertJsonEquals(merged["ext"], """{"enabled":false,"x":1}""");
    }

    private static Dictionary<string, JsonElement> BuildConfig(string extensionId, string json)
    {
        var element = JsonSerializer.Deserialize<JsonElement>(json);
        return new Dictionary<string, JsonElement> { [extensionId] = element };
    }

    private static void AssertJsonEquals(JsonElement actual, string expectedJson)
    {
        var actualNode = JsonNode.Parse(actual.GetRawText());
        var expectedNode = JsonNode.Parse(expectedJson);
        JsonNode.DeepEquals(actualNode, expectedNode).Should().BeTrue();
    }
}

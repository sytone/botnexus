using System.Text.Json;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Configuration.Tests;

public sealed class NullableOpaqueConfigPathTests
{
    [Theory]
    [InlineData("metadata", false)]
    [InlineData("metadata", true)]
    [InlineData("isolationOptions", false)]
    [InlineData("isolationOptions", true)]
    public void WholeNullableProperty_ReplacementAndClrNullClear_PreserveSiblings(string property, bool populated)
    {
        var agent = new AgentDefinitionConfig { DisplayName = "sibling" };
        if (populated)
            Set(agent, property, JsonSerializer.Deserialize<JsonElement>("{\"old\":true}"));
        var config = new PlatformConfig { Agents = new() { ["alpha"] = agent } };
        var resolver = new ConfigPathResolver();
        var path = "agents.alpha." + property;
        resolver.TrySetValue(config, path, "{\"new\":[1,null]}", out var error).ShouldBeTrue(error);
        Get(agent, property).ShouldNotBeNull().GetProperty("new").GetArrayLength().ShouldBe(2);
        agent.DisplayName.ShouldBe("sibling");
        resolver.TrySetValue(config, path, "null", out error).ShouldBeTrue(error);
        Get(agent, property).HasValue.ShouldBeFalse("nullable CLR property must clear, not contain JsonValueKind.Null");
        resolver.TryGetValue(config, path, out var value, out error).ShouldBeTrue(error);
        value.ShouldBeNull();
        resolver.TrySetValue(config, path, JsonSerializer.Deserialize<JsonElement>("{\"typed\":true}"), out error).ShouldBeTrue(error);
        resolver.TrySetValue(config, path, null, out error).ShouldBeTrue(error);
        Get(agent, property).HasValue.ShouldBeFalse();
        agent.DisplayName.ShouldBe("sibling");
    }

    [Theory]
    [InlineData("metadata", false)]
    [InlineData("metadata", true)]
    [InlineData("isolationOptions", false)]
    [InlineData("isolationOptions", true)]
    public void NestedNullableProperty_SharedStrictEdit_IsAtomic(string property, bool populated)
    {
        var agent = new AgentDefinitionConfig { DisplayName = "sibling" };
        if (populated)
            Set(agent, property, JsonSerializer.Deserialize<JsonElement>("{\"keep\":17,\"parent\":{\"old\":true}}"));
        var config = new PlatformConfig { Agents = new() { ["alpha"] = agent } };
        var resolver = new ConfigPathResolver();
        var path = "agents.alpha." + property;
        var before = Get(agent, property);
        resolver.TrySetValue(config, path + ".newParent.items[2]", "true", out var error).ShouldBeFalse();
        Get(agent, property).HasValue.ShouldBe(before.HasValue);
        if (before is { } original)
            Get(agent, property).ShouldNotBeNull().GetRawText().ShouldBe(original.GetRawText());
        resolver.TrySetValue(config, path + ".parent.added", "true", out error).ShouldBeTrue(error);
        resolver.TryGetValue(config, path + ".parent.added", out var value, out error).ShouldBeTrue(error);
        JsonSerializer.Serialize(value).ShouldBe("true");
        var current = Get(agent, property).ShouldNotBeNull();
        if (populated)
        {
            current.GetProperty("keep").GetInt32().ShouldBe(17);
            current.GetProperty("parent").GetProperty("old").GetBoolean().ShouldBeTrue();
        }
        agent.DisplayName.ShouldBe("sibling");
    }

    private static JsonElement? Get(AgentDefinitionConfig agent, string property)
        => property == "metadata" ? agent.Metadata : agent.IsolationOptions;

    private static void Set(AgentDefinitionConfig agent, string property, JsonElement value)
    {
        if (property == "metadata")
            agent.Metadata = value;
        else
            agent.IsolationOptions = value;
    }
}

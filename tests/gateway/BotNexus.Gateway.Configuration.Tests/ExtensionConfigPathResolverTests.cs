using System.Text.Json;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Configuration.Tests;

public sealed class ExtensionConfigPathResolverTests
{
    private readonly ConfigPathResolver _resolver = new();

    private static PlatformConfig CreateConfig() => new()
    {
        Gateway = new GatewaySettingsConfig
        {
            Extensions = new()
            {
                ["Sample"] = JsonSerializer.SerializeToElement(new
                {
                    Settings = new { Enabled = true, Label = "old", Keep = 17 },
                    Items = new object?[] { new { Name = "first", Keep = true }, "second", null },
                    Scalar = 42
                })
            }
        }
    };

    [Theory]
    [InlineData("gateway.extensions.sample.settings.enabled", "true")]
    [InlineData("GATEWAY.EXTENSIONS.SAMPLE.SETTINGS.LABEL", "\"old\"")]
    [InlineData("gateway.extensions.sample.items[0].name", "\"first\"")]
    [InlineData("gateway.extensions.sample.items[2]", "null")]
    public void TryGetValue_JsonChildren_ReturnsAddressedValue(string path, string expected)
    {
        _resolver.TryGetValue(CreateConfig(), path, out var value, out var error).ShouldBeTrue(error);
        JsonSerializer.Serialize(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("settings.enabled", "false", "false")]
    [InlineData("settings.label", "new label", "\"new label\"")]
    [InlineData("settings.added", "123", "123")]
    [InlineData("settings.added", "{\"nested\":[]}", "{\"nested\":[]}")]
    [InlineData("settings.added", "[true,null,{}]", "[true,null,{}]")]
    [InlineData("settings.label", "null", "null")]
    [InlineData("items[0].name", "updated", "\"updated\"")]
    [InlineData("items[3]", "appended", "\"appended\"")]
    [InlineData("newParent.child", "true", "true")]
    [InlineData("newArray[0].name", "created", "\"created\"")]
    public void TrySetValue_JsonChildren_PreservesSiblings(string suffix, string raw, string expected)
    {
        var config = CreateConfig();
        var path = "gateway.extensions.sample." + suffix;
        _resolver.TrySetValue(config, path, raw, out var error).ShouldBeTrue(error);
        _resolver.TryGetValue(config, path, out var value, out error).ShouldBeTrue(error);
        JsonSerializer.Serialize(value).ShouldBe(expected);
        var extension = config.Gateway.ShouldNotBeNull().Extensions.ShouldNotBeNull()["Sample"];
        extension.GetProperty("Settings").GetProperty("Keep").GetInt32().ShouldBe(17);
        extension.GetProperty("Items")[0].GetProperty("Keep").GetBoolean().ShouldBeTrue();
        extension.GetProperty("Scalar").GetInt32().ShouldBe(42);
        extension.EnumerateObject().Count().ShouldBe(suffix.StartsWith("new", StringComparison.Ordinal) ? 4 : 3);
    }

    [Theory]
    [InlineData("scalar.child", "x")]
    [InlineData("settings[0]", "x")]
    [InlineData("items[-1]", "x")]
    [InlineData("items[5]", "x")]
    [InlineData("items[1].child", "x")]
    [InlineData("newParent.child[2]", "x")]
    public void TrySetValue_InvalidJsonPath_IsAtomicAndNamesFullPath(string suffix, string raw)
    {
        var config = CreateConfig();
        var before = JsonSerializer.Serialize(config);
        var path = "gateway.extensions.sample." + suffix;
        _resolver.TrySetValue(config, path, raw, out var error).ShouldBeFalse();
        error.ShouldContain(path);
        JsonSerializer.Serialize(config).ShouldBe(before);
    }

    [Theory]
    [InlineData("settings.missing")]
    [InlineData("items[9]")]
    [InlineData("scalar.child")]
    public void TryGetValue_InvalidJsonPath_NamesFullPath(string suffix)
    {
        var path = "gateway.extensions.sample." + suffix;
        _resolver.TryGetValue(CreateConfig(), path, out _, out var error).ShouldBeFalse();
        error.ShouldContain(path);
    }

    [Fact]
    public void TrySetValue_AbsentExtension_CreatesJsonObject()
    {
        var config = CreateConfig();
        const string path = "gateway.extensions.new-extension.parent.enabled";
        _resolver.TrySetValue(config, path, "true", out var error).ShouldBeTrue(error);
        _resolver.TryGetValue(config, path, out var value, out error).ShouldBeTrue(error);
        JsonSerializer.Serialize(value).ShouldBe("true");
    }

    [Fact]
    public void TrySetValue_AgentDefaults_PreservesLegacyAgentLeafCoercionAlongsideDeclaredDefaults()
    {
        var config = new PlatformConfig();
        const string thinkingPath = "agents.defaults.thinking";
        _resolver.TrySetValue(config, thinkingPath, "high", out var error).ShouldBeTrue(error);
        _resolver.TryGetValue(config, thinkingPath, out var thinking, out error).ShouldBeTrue(error);
        thinking.ShouldBe("high");

        const string extensionPath = "agents.defaults.extensions.sample.enabled";
        _resolver.TrySetValue(config, extensionPath, "true", out error).ShouldBeTrue(error);
        _resolver.TryGetValue(config, extensionPath, out var enabled, out error).ShouldBeTrue(error);
        JsonSerializer.Serialize(enabled).ShouldBe("true");
        config.AgentDefaults.ShouldNotBeNull().Extensions.ShouldNotBeNull()["sample"]
            .GetProperty("enabled").GetBoolean().ShouldBeTrue();
        _resolver.TryGetValue(config, thinkingPath, out thinking, out error).ShouldBeTrue(error);
        thinking.ShouldBe("high");
    }

    [Fact]
    public void ConfigDocument_NullAndRemove_AreDistinctForJsonChildren()
    {
        var document = ConfigDocument.Parse("""{"gateway":{"extensions":{"sample":{"items":[1,2],"setting":true}}}}""");
        document.TrySetFrom("gateway.extensions.sample.setting", null, out var error).ShouldBeTrue(error);
        document.Exists("gateway.extensions.sample.setting").ShouldBeTrue();
        document.TryRemove("gateway.extensions.sample.setting", out error).ShouldBeTrue(error);
        document.Exists("gateway.extensions.sample.setting").ShouldBeFalse();
        document.TryRemove("gateway.extensions.sample.items[0]", out error).ShouldBeTrue(error);
        document.GetInt("gateway.extensions.sample.items[0]").ShouldBe(2);
    }
}

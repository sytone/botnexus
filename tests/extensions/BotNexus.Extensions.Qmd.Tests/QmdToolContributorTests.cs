using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Qmd.Tests;

public sealed class QmdToolContributorTests
{
    [Fact]
    public void ResolveConfig_WithValidExtensionConfig_ReturnsConfig()
    {
        var configJson = JsonSerializer.SerializeToElement(new { enabled = true, qmdPath = "/usr/bin/qmd", maxResults = 20 });
        var descriptor = CreateDescriptor(new Dictionary<string, JsonElement> { ["botnexus-qmd"] = configJson });

        var config = QmdToolContributor.ResolveConfig(descriptor);

        Assert.True(config.Enabled);
        Assert.Equal("/usr/bin/qmd", config.QmdPath);
        Assert.Equal(20, config.MaxResults);
    }

    [Fact]
    public void ResolveConfig_WithNoExtensionConfig_ReturnsDefaults()
    {
        var descriptor = CreateDescriptor(new Dictionary<string, JsonElement>());
        var config = QmdToolContributor.ResolveConfig(descriptor);

        Assert.True(config.Enabled);
        Assert.Null(config.QmdPath);
        Assert.Equal(10, config.MaxResults);
        Assert.Equal("hybrid", config.DefaultSearchMode);
    }

    [Fact]
    public void ResolveConfig_WithDisabledConfig_ReturnsDisabled()
    {
        var configJson = JsonSerializer.SerializeToElement(new { enabled = false });
        var descriptor = CreateDescriptor(new Dictionary<string, JsonElement> { ["botnexus-qmd"] = configJson });

        var config = QmdToolContributor.ResolveConfig(descriptor);
        Assert.False(config.Enabled);
    }

    [Fact]
    public void ResolveConfig_WithMalformedJson_ReturnsDefaults()
    {
        var malformed = JsonSerializer.SerializeToElement("not an object");
        var descriptor = CreateDescriptor(new Dictionary<string, JsonElement> { ["botnexus-qmd"] = malformed });

        var config = QmdToolContributor.ResolveConfig(descriptor);
        Assert.True(config.Enabled); // Falls back to defaults
    }

    private static AgentDescriptor CreateDescriptor(Dictionary<string, JsonElement> extensionConfig)
    {
        return new AgentDescriptor
        {
            AgentId = AgentId.From("test-agent"),
            DisplayName = "Test Agent",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ExtensionConfig = extensionConfig
        };
    }
}

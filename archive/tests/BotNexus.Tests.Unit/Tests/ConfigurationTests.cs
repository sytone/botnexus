using BotNexus.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BotNexus.Tests.Unit.Tests;

public class ConfigurationTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    [Fact]
    public void BotNexusConfig_Binds_DefaultValues()
    {
        var config = BuildConfig(new Dictionary<string, string?>());
        var botNexusConfig = new BotNexusConfig();

        botNexusConfig.Agents.Model.Should().BeNull(); // Model is now nullable
        botNexusConfig.Agents.MaxTokens.Should().BeNull();
        botNexusConfig.Agents.Temperature.Should().BeNull();
        botNexusConfig.Gateway.Port.Should().Be(18790);
        botNexusConfig.Api.Port.Should().Be(8900);
    }

    [Fact]
    public void BotNexusConfig_BindsFromConfiguration()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["BotNexus:Agents:Model"] = "claude-3-5-sonnet",
            ["BotNexus:Agents:MaxTokens"] = "16384",
            ["BotNexus:Gateway:Port"] = "9000",
            ["BotNexus:Providers:openai:ApiKey"] = "sk-test123",
            ["BotNexus:Channels:Instances:telegram:Enabled"] = "true",
            ["BotNexus:Channels:Instances:telegram:BotToken"] = "bot-token-123"
        });

        var botNexusConfig = new BotNexusConfig();
        config.GetSection(BotNexusConfig.SectionName).Bind(botNexusConfig);

        botNexusConfig.Agents.Model.Should().Be("claude-3-5-sonnet");
        botNexusConfig.Agents.MaxTokens.Should().Be(16384);
        botNexusConfig.Gateway.Port.Should().Be(9000);
        botNexusConfig.Providers["openai"].ApiKey.Should().Be("sk-test123");
        botNexusConfig.Channels.Instances["telegram"].Enabled.Should().BeTrue();
        botNexusConfig.Channels.Instances["telegram"].BotToken.Should().Be("bot-token-123");
    }

    [Fact]
    public void ChannelsConfig_AllowFrom_ParsedAsList()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["BotNexus:Channels:Instances:telegram:AllowFrom:0"] = "user1",
            ["BotNexus:Channels:Instances:telegram:AllowFrom:1"] = "user2",
            ["BotNexus:Channels:Instances:telegram:AllowFrom:2"] = "user3"
        });

        var botNexusConfig = new BotNexusConfig();
        config.GetSection(BotNexusConfig.SectionName).Bind(botNexusConfig);

        botNexusConfig.Channels.Instances["telegram"].AllowFrom.Should().HaveCount(3);
        botNexusConfig.Channels.Instances["telegram"].AllowFrom.Should().Contain("user1");
    }

    [Fact]
    public void HeartbeatConfig_DefaultValues_AreCorrect()
    {
        var config = new HeartbeatConfig();
        config.Enabled.Should().BeTrue();
        config.IntervalSeconds.Should().Be(1800);
    }

    [Fact]
    public void ToolsConfig_DefaultValues_AreCorrect()
    {
        var config = new ToolsConfig();
        config.RestrictToWorkspace.Should().BeFalse();
        config.Exec.Enable.Should().BeTrue();
        config.Exec.Timeout.Should().Be(60);
        config.Web.Search.MaxResults.Should().Be(5);
        config.Extensions.Should().BeEmpty();
    }
}

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

        botNexusConfig.Agents.Model.Should().Be("gpt-4o");
        botNexusConfig.Agents.MaxTokens.Should().Be(8192);
        botNexusConfig.Agents.Temperature.Should().Be(0.1);
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
            ["BotNexus:Providers:OpenAI:ApiKey"] = "sk-test123",
            ["BotNexus:Channels:Telegram:Enabled"] = "true",
            ["BotNexus:Channels:Telegram:BotToken"] = "bot-token-123"
        });

        var botNexusConfig = new BotNexusConfig();
        config.GetSection(BotNexusConfig.SectionName).Bind(botNexusConfig);

        botNexusConfig.Agents.Model.Should().Be("claude-3-5-sonnet");
        botNexusConfig.Agents.MaxTokens.Should().Be(16384);
        botNexusConfig.Gateway.Port.Should().Be(9000);
        botNexusConfig.Providers.OpenAI.ApiKey.Should().Be("sk-test123");
        botNexusConfig.Channels.Telegram.Enabled.Should().BeTrue();
        botNexusConfig.Channels.Telegram.BotToken.Should().Be("bot-token-123");
    }

    [Fact]
    public void ChannelsConfig_AllowFrom_ParsedAsList()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["BotNexus:Channels:Telegram:AllowFrom:0"] = "user1",
            ["BotNexus:Channels:Telegram:AllowFrom:1"] = "user2",
            ["BotNexus:Channels:Telegram:AllowFrom:2"] = "user3"
        });

        var botNexusConfig = new BotNexusConfig();
        config.GetSection(BotNexusConfig.SectionName).Bind(botNexusConfig);

        botNexusConfig.Channels.Telegram.AllowFrom.Should().HaveCount(3);
        botNexusConfig.Channels.Telegram.AllowFrom.Should().Contain("user1");
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
    }
}

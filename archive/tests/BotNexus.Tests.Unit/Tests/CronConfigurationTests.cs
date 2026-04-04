using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Cron.Jobs;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace BotNexus.Tests.Unit.Tests;

public sealed class CronConfigurationTests
{
    [Fact]
    public void CronConfig_BindsJobsAndJobSettings()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotNexus:Cron:Enabled"] = "true",
                ["BotNexus:Cron:TickIntervalSeconds"] = "3",
                ["BotNexus:Cron:ExecutionHistorySize"] = "20",
                ["BotNexus:Cron:Jobs:morning:Type"] = "agent",
                ["BotNexus:Cron:Jobs:morning:Schedule"] = "0 8 * * *",
                ["BotNexus:Cron:Jobs:morning:Agent"] = "bender",
                ["BotNexus:Cron:Jobs:morning:Prompt"] = "Morning brief",
                ["BotNexus:Cron:Jobs:morning:Session"] = "persistent",
                ["BotNexus:Cron:Jobs:morning:OutputChannels:0"] = "websocket",
                ["BotNexus:Cron:Jobs:maintenance:Type"] = "maintenance",
                ["BotNexus:Cron:Jobs:maintenance:Schedule"] = "0 2 * * *",
                ["BotNexus:Cron:Jobs:maintenance:Action"] = "consolidate-memory",
                ["BotNexus:Cron:Jobs:maintenance:Agents:0"] = "bender",
                ["BotNexus:Cron:Jobs:maintenance:Agents:1"] = "leela"
            })
            .Build();

        var botNexusConfig = new BotNexusConfig();
        config.GetSection(BotNexusConfig.SectionName).Bind(botNexusConfig);

        botNexusConfig.Cron.Enabled.Should().BeTrue();
        botNexusConfig.Cron.TickIntervalSeconds.Should().Be(3);
        botNexusConfig.Cron.ExecutionHistorySize.Should().Be(20);
        botNexusConfig.Cron.Jobs.Should().ContainKeys("morning", "maintenance");

        var morning = botNexusConfig.Cron.Jobs["morning"];
        morning.Agent.Should().Be("bender");
        morning.Prompt.Should().Be("Morning brief");
        morning.OutputChannels.Should().ContainSingle().Which.Should().Be("websocket");

        var maintenance = botNexusConfig.Cron.Jobs["maintenance"];
        maintenance.Action.Should().Be("consolidate-memory");
        maintenance.Agents.Should().Equal("bender", "leela");
    }

    [Fact]
    public void CronJobConfig_Defaults_AreInitialized()
    {
        var config = new CronJobConfig();

        config.Schedule.Should().BeEmpty();
        config.Type.Should().Be("agent");
        config.Enabled.Should().BeTrue();
        config.SessionCleanupDays.Should().Be(30);
        config.LogRetentionDays.Should().Be(30);
        config.Agents.Should().NotBeNull().And.BeEmpty();
        config.OutputChannels.Should().NotBeNull().And.BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CronJobConfig_AgentValidation_RequiresPrompt(string? prompt)
    {
        var config = new CronJobConfig
        {
            Schedule = "* * * * *",
            Type = "agent",
            Agent = "bender",
            Prompt = prompt
        };

        var runnerFactory = new Mock<IAgentRunnerFactory>();
        var sessionManager = new Mock<ISessionManager>();

        var act = () => new AgentCronJob(
            config,
            runnerFactory.Object,
            sessionManager.Object,
            _ => null);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Prompt must be provided*");
    }
}

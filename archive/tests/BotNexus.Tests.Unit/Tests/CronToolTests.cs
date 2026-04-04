using BotNexus.Agent.Tools;
using BotNexus.Core.Abstractions;
using BotNexus.Cron.Jobs;
using FluentAssertions;
using Moq;

namespace BotNexus.Tests.Unit.Tests;

public sealed class CronToolTests
{
    [Fact]
    public async Task ExecuteAsync_ScheduleAction_RegistersAgentCronJob()
    {
        var cronService = new Mock<ICronService>();
        var runnerFactory = new Mock<IAgentRunnerFactory>();
        var sessionManager = new Mock<ISessionManager>();
        var tool = new CronTool(cronService.Object, runnerFactory.Object, sessionManager.Object, []);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["action"] = "schedule",
            ["name"] = "daily-digest",
            ["schedule"] = "0 9 * * *",
            ["agent"] = "bender",
            ["prompt"] = "Send me a daily digest.",
            ["session"] = "persistent",
            ["enabled"] = true,
            ["output_channels"] = new[] { "websocket" }
        });

        result.Should().Contain("daily-digest");
        cronService.Verify(
            x => x.Register(It.Is<ICronJob>(job =>
                job is AgentCronJob &&
                job.Name == "daily-digest" &&
                job.Schedule == "0 9 * * *" &&
                job.Enabled)),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ScheduleAction_WithoutDependencies_ReturnsError()
    {
        var cronService = new Mock<ICronService>();
        var tool = new CronTool(cronService.Object);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["action"] = "schedule",
            ["agent"] = "bender",
            ["prompt"] = "Hello",
            ["schedule"] = "0 9 * * *"
        });

        result.Should().Contain("not available");
        cronService.Verify(x => x.Register(It.IsAny<ICronJob>()), Times.Never);
    }
}

using BotNexus.Core.Abstractions;
using BotNexus.Heartbeat;
using FluentAssertions;
using Moq;

namespace BotNexus.Tests.Unit.Tests;

public sealed class HeartbeatServiceTests
{
    [Fact]
    public void Beat_IsNoOp()
    {
        var cron = new Mock<ICronService>();
        var sut = new HeartbeatService(cron.Object);

        var act = () => sut.Beat();

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsHealthy_DelegatesToCronServiceState(bool isRunning)
    {
        var cron = new Mock<ICronService>();
        cron.SetupGet(c => c.IsRunning).Returns(isRunning);

        var sut = new HeartbeatService(cron.Object);

        sut.IsHealthy.Should().Be(isRunning);
    }

    [Fact]
    public void LastBeat_ReturnsMostRecentCronJobRun()
    {
        var oldest = new DateTimeOffset(2026, 4, 3, 9, 0, 0, TimeSpan.Zero);
        var newest = new DateTimeOffset(2026, 4, 3, 11, 0, 0, TimeSpan.Zero);
        var cron = new Mock<ICronService>();
        cron.Setup(c => c.GetJobs()).Returns(
        [
            new CronJobStatus("job-a", CronJobType.Agent, "0 * * * *", true, oldest, null, true, TimeSpan.FromSeconds(2)),
            new CronJobStatus("job-b", CronJobType.System, "*/5 * * * *", true, null, null, null, null),
            new CronJobStatus("job-c", CronJobType.Maintenance, "0 2 * * *", true, newest, null, true, TimeSpan.FromSeconds(1))
        ]);

        var sut = new HeartbeatService(cron.Object);

        sut.LastBeat.Should().Be(newest);
    }
}

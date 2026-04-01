using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Gateway.HealthChecks;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Tests.Unit.Tests;

public sealed class CronServiceHealthCheckTests
{
    [Fact]
    public async Task Disabled_ReportsHealthy()
    {
        var cronService = new Mock<ICronService>();
        var config = Options.Create(new CronConfig { Enabled = false });
        var check = new CronServiceHealthCheck(cronService.Object, config);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("disabled");
    }

    [Fact]
    public async Task NotRunning_ReportsUnhealthy()
    {
        var cronService = new Mock<ICronService>();
        cronService.Setup(x => x.IsRunning).Returns(false);
        var config = Options.Create(new CronConfig { Enabled = true });
        var check = new CronServiceHealthCheck(cronService.Object, config);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("not running");
    }

    [Fact]
    public async Task Running_NoFailures_ReportsHealthy()
    {
        var cronService = new Mock<ICronService>();
        cronService.Setup(x => x.IsRunning).Returns(true);
        cronService.Setup(x => x.GetJobs()).Returns(
        [
            new CronJobStatus("job-a", CronJobType.Agent, "* * * * *", true, null, null, true, null, 0),
            new CronJobStatus("job-b", CronJobType.System, "*/5 * * * *", true, null, null, true, null, 1)
        ]);
        var config = Options.Create(new CronConfig { Enabled = true });
        var check = new CronServiceHealthCheck(cronService.Object, config);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Running_WithConsecutiveFailures_ReportsDegraded()
    {
        var cronService = new Mock<ICronService>();
        cronService.Setup(x => x.IsRunning).Returns(true);
        cronService.Setup(x => x.GetJobs()).Returns(
        [
            new CronJobStatus("job-a", CronJobType.Agent, "* * * * *", true, null, null, false, null, 3),
            new CronJobStatus("job-b", CronJobType.System, "*/5 * * * *", true, null, null, true, null, 0)
        ]);
        var config = Options.Create(new CronConfig { Enabled = true });
        var check = new CronServiceHealthCheck(cronService.Object, config);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("job-a");
        result.Description.Should().Contain("3 consecutive failures");
    }

    [Fact]
    public async Task Running_BelowThreshold_ReportsHealthy()
    {
        var cronService = new Mock<ICronService>();
        cronService.Setup(x => x.IsRunning).Returns(true);
        cronService.Setup(x => x.GetJobs()).Returns(
        [
            new CronJobStatus("job-a", CronJobType.Agent, "* * * * *", true, null, null, false, null, 2)
        ]);
        var config = Options.Create(new CronConfig { Enabled = true });
        var check = new CronServiceHealthCheck(cronService.Object, config);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }
}

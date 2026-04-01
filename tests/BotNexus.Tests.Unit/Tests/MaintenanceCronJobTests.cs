using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using BotNexus.Cron.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BotNexus.Tests.Unit.Tests;

public sealed class MaintenanceCronJobTests
{
    [Fact]
    public async Task ExecuteAsync_ConsolidatesConfiguredAgents()
    {
        var config = new CronJobConfig
        {
            Schedule = "* * * * *",
            Action = "consolidate-memory",
            Agents = ["bender", "leela"]
        };

        var consolidator = new Mock<IMemoryConsolidator>();
        consolidator.Setup(c => c.ConsolidateAsync("bender", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryConsolidationResult(true, 2, 5));
        consolidator.Setup(c => c.ConsolidateAsync("leela", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryConsolidationResult(true, 1, 3));

        var sessionManager = new Mock<ISessionManager>();
        var job = new MaintenanceCronJob(config, consolidator.Object, sessionManager.Object);

        var result = await job.ExecuteAsync(BuildContext("memory"), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Metadata.Should().NotBeNull();
        result.Metadata!["agentsProcessed"].Should().Be(2);
        result.Metadata["dailyFilesProcessed"].Should().Be(3);
        result.Metadata["entriesConsolidated"].Should().Be(8);
        consolidator.Verify(c => c.ConsolidateAsync("bender", It.IsAny<CancellationToken>()), Times.Once);
        consolidator.Verify(c => c.ConsolidateAsync("leela", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_CleanupSessions_DeletesOnlyOldSessions()
    {
        var config = new CronJobConfig
        {
            Schedule = "* * * * *",
            Action = "cleanup-sessions",
            SessionCleanupDays = 30
        };

        var now = DateTimeOffset.UtcNow;
        var oldSession = new BotNexus.Core.Models.Session
        {
            Key = "old",
            AgentName = "agent",
            History = [new SessionEntry(MessageRole.User, "old", now.AddDays(-45))]
        };
        var freshSession = new BotNexus.Core.Models.Session
        {
            Key = "fresh",
            AgentName = "agent",
            History = [new SessionEntry(MessageRole.User, "fresh", now.AddDays(-3))]
        };

        var consolidator = new Mock<IMemoryConsolidator>();
        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(s => s.ListKeysAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["old", "fresh"]);
        sessionManager.Setup(s => s.GetOrCreateAsync("old", "maintenance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(oldSession);
        sessionManager.Setup(s => s.GetOrCreateAsync("fresh", "maintenance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(freshSession);

        var job = new MaintenanceCronJob(config, consolidator.Object, sessionManager.Object);
        var result = await job.ExecuteAsync(BuildContext("cleanup"), CancellationToken.None);

        result.Success.Should().BeTrue();
        sessionManager.Verify(s => s.DeleteAsync("old", It.IsAny<CancellationToken>()), Times.Once);
        sessionManager.Verify(s => s.DeleteAsync("fresh", It.IsAny<CancellationToken>()), Times.Never);
        result.Metadata.Should().NotBeNull();
        result.Metadata!["sessionsDeleted"].Should().Be(1);
    }

    private static CronJobContext BuildContext(string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new CronJobContext
        {
            JobName = name,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ScheduledTime = now,
            ActualTime = now,
            Services = new ServiceCollection().BuildServiceProvider()
        };
    }
}

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

    [Fact]
    public async Task ExecuteAsync_ConsolidateMemory_WithNoAgents_ReturnsNoopSuccess()
    {
        var config = new CronJobConfig
        {
            Schedule = "* * * * *",
            Action = "consolidate-memory"
        };

        var consolidator = new Mock<IMemoryConsolidator>();
        var sessionManager = new Mock<ISessionManager>();
        var job = new MaintenanceCronJob(config, consolidator.Object, sessionManager.Object);

        var result = await job.ExecuteAsync(BuildContext("memory"), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("No agents configured for memory consolidation.");
        result.Metadata.Should().NotBeNull();
        result.Metadata!["agentsProcessed"].Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ConsolidateMemory_ReturnsFailureWhenAnyAgentFails()
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
            .ReturnsAsync(new MemoryConsolidationResult(false, 1, 1));

        var sessionManager = new Mock<ISessionManager>();
        var job = new MaintenanceCronJob(config, consolidator.Object, sessionManager.Object);

        var result = await job.ExecuteAsync(BuildContext("memory"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Metadata.Should().NotBeNull();
        result.Metadata!["agentsSucceeded"].Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_RotateLogs_ArchivesOldFiles()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "test-artifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var oldFile = Path.Combine(root, "old.log");
            var freshFile = Path.Combine(root, "fresh.log");
            File.WriteAllText(oldFile, "old");
            File.WriteAllText(freshFile, "fresh");
            File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-45));
            File.SetLastWriteTimeUtc(freshFile, DateTime.UtcNow.AddDays(-1));

            var config = new CronJobConfig
            {
                Schedule = "* * * * *",
                Action = "rotate-logs",
                LogsPath = root,
                LogRetentionDays = 30
            };

            var consolidator = new Mock<IMemoryConsolidator>();
            var sessionManager = new Mock<ISessionManager>();
            var job = new MaintenanceCronJob(config, consolidator.Object, sessionManager.Object);

            var result = await job.ExecuteAsync(BuildContext("rotate"), CancellationToken.None);

            result.Success.Should().BeTrue();
            result.Metadata.Should().NotBeNull();
            result.Metadata!["archivedFiles"].Should().Be(1);
            File.Exists(Path.Combine(root, "archive", "old.log")).Should().BeTrue();
            File.Exists(freshFile).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_UnknownAction_ReturnsFailure()
    {
        var config = new CronJobConfig
        {
            Schedule = "* * * * *",
            Action = "does-not-exist"
        };

        var consolidator = new Mock<IMemoryConsolidator>();
        var sessionManager = new Mock<ISessionManager>();
        var job = new MaintenanceCronJob(config, consolidator.Object, sessionManager.Object);

        var result = await job.ExecuteAsync(BuildContext("maintenance"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Unknown maintenance action 'does-not-exist'.");
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

using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Cron.Tests;

/// <summary>
/// RED scheduler tests for issue #4124. Startup migration must use the portable narrow store
/// operation once per job and retain cancellation and per-job failure isolation.
/// </summary>
public sealed class CronSessionPrefixRebindMigrationTests
{
    [Fact]
    public async Task MigrateLegacyCronConversationsAsync_MultipleJobs_UsesNarrowRebindAndNeverListsSessionAggregates()
    {
        var agent = AgentId.From("agent-a");
        var jobs = new[] { CreateJob("job-1", agent), CreateJob("job-2", agent) };
        var (scheduler, sessions) = await CreateSchedulerAsync(jobs);
        sessions.Setup(store => store.RebindSessionsAsync(agent, It.IsAny<string>(), It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        sessions.Setup(store => store.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("full aggregate enumeration is forbidden"));

        await scheduler.MigrateLegacyCronConversationsAsync(CancellationToken.None);

        sessions.Verify(store => store.RebindSessionsAsync(agent, "cron:job-1:", It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()), Times.Once);
        sessions.Verify(store => store.RebindSessionsAsync(agent, "cron:job-2:", It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()), Times.Once);
        sessions.Verify(store => store.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MigrateLegacyCronConversationsAsync_RebindFailureOnOneJob_ContinuesWithNextJob()
    {
        var agent = AgentId.From("agent-a");
        var jobs = new[] { CreateJob("job-1", agent), CreateJob("job-2", agent) };
        var (scheduler, sessions) = await CreateSchedulerAsync(jobs);
        sessions.Setup(store => store.RebindSessionsAsync(agent, "cron:job-1:", It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("persistence unavailable"));
        sessions.Setup(store => store.RebindSessionsAsync(agent, "cron:job-2:", It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        await scheduler.MigrateLegacyCronConversationsAsync(CancellationToken.None);

        sessions.Verify(store => store.RebindSessionsAsync(agent, "cron:job-2:", It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MigrateLegacyCronConversationsAsync_CancellationFromRebindIsPropagatedAndStopsLaterJobs()
    {
        var agent = AgentId.From("agent-a");
        var jobs = new[] { CreateJob("job-1", agent), CreateJob("job-2", agent) };
        var (scheduler, sessions) = await CreateSchedulerAsync(jobs);
        using var cancellation = new CancellationTokenSource();
        sessions.Setup(store => store.RebindSessionsAsync(agent, "cron:job-1:", It.IsAny<ConversationId>(), cancellation.Token))
            .Returns(() => { cancellation.Cancel(); return Task.FromCanceled<int>(cancellation.Token); });

        Func<Task> act = () => scheduler.MigrateLegacyCronConversationsAsync(cancellation.Token);
        await Should.ThrowAsync<OperationCanceledException>(act);
        sessions.Verify(store => store.RebindSessionsAsync(agent, "cron:job-2:", It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static CronJob CreateJob(string id, AgentId agent) => CronStoreTestContext.CreateJob(id) with
    {
        AgentId = agent,
        Name = $"Job {id}"
    };

    private static async Task<(CronScheduler Scheduler, Mock<ISessionStore> Sessions)> CreateSchedulerAsync(IReadOnlyList<CronJob> jobs)
    {
        var cronStore = new Mock<ICronStore>(MockBehavior.Strict);
        cronStore.Setup(store => store.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        cronStore.Setup(store => store.ListAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(jobs);
        cronStore.Setup(store => store.TrySetConversationIdAsync(It.IsAny<JobId>(), It.IsAny<ConversationId>(), It.IsAny<CancellationToken>())).ReturnsAsync((JobId _, ConversationId conversationId, CancellationToken _) => (ConversationId?)conversationId);

        var canonicalByJob = jobs.ToDictionary(
            job => job.Id.Value,
            job => new Conversation
            {
                ConversationId = ConversationId.Create(),
                AgentId = job.AgentId!.Value,
                Title = $"cron:{job.Id.Value}",
                Status = ConversationStatus.Active
            });
        var conversations = new Mock<IConversationStore>(MockBehavior.Strict);
        conversations.Setup(store => store.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentId? agent, CancellationToken _) => canonicalByJob.Values.Where(value => agent is null || value.AgentId == agent.Value).ToList());
        conversations.Setup(store => store.GetAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConversationId id, CancellationToken _) => canonicalByJob.Values.SingleOrDefault(value => value.ConversationId == id));

        var sessions = new Mock<ISessionStore>(MockBehavior.Strict);
        var services = new ServiceCollection();
        services.AddSingleton<IConversationStore>(conversations.Object);
        services.AddSingleton(sessions.Object);
        var provider = services.BuildServiceProvider();
        var scheduler = new CronScheduler(
            cronStore.Object,
            [],
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(new CronOptions { Enabled = true }),
            NullLogger<CronScheduler>.Instance);
        return (scheduler, sessions);
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;
        public T Get(string? name) => currentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

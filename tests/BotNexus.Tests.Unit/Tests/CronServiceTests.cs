using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Extensions;
using BotNexus.Core.Models;
using BotNexus.Core.Observability;
using BotNexus.Cron;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Tests.Unit.Tests;

public sealed class CronServiceTests
{
    [Fact]
    public void RegisterAndRemove_ManageJobCatalog()
    {
        var service = CreateService();
        var job = new TestCronJob("job-a", "* * * * * *");

        service.Register(job);
        service.GetJobs().Should().ContainSingle(j => j.Name == "job-a");

        service.Remove("job-a");
        service.GetJobs().Should().BeEmpty();
    }

    [Fact]
    public async Task TriggerAsync_ExecutesJobAndRecordsHistory()
    {
        var activity = new TestActivityStream();
        var service = CreateService(activityStream: activity);
        var job = new TestCronJob("job-a", "* * * * * *");
        service.Register(job);

        await service.TriggerAsync("job-a");
        await WaitForAsync(() => job.ExecutionCount == 1);

        var history = service.GetHistory("job-a");
        history.Should().ContainSingle();
        history[0].JobName.Should().Be("job-a");
        history[0].Success.Should().BeTrue();

        activity.Events.Should().HaveCount(2);
        activity.Events.Any(e => e.Metadata is not null && Equals(e.Metadata["event"], "cron.job.started")).Should().BeTrue();
        activity.Events.Any(e => e.Metadata is not null && Equals(e.Metadata["event"], "cron.job.completed")).Should().BeTrue();
    }

    [Fact]
    public async Task TriggerAsync_WhileRunning_DoesNotStartOverlap()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateService();
        var job = new TestCronJob("job-a", "* * * * * *")
        {
            ExecuteHandler = async (_, _) =>
            {
                await gate.Task;
                return new CronJobResult(true, "done", Duration: TimeSpan.Zero);
            }
        };
        service.Register(job);

        await service.TriggerAsync("job-a");
        await service.TriggerAsync("job-a");
        await Task.Delay(100);

        job.ExecutionCount.Should().Be(1);

        gate.SetResult();
        await WaitForAsync(() => service.GetHistory("job-a").Count == 1);
    }

    [Fact]
    public async Task TriggerAsync_UnknownJob_Throws()
    {
        var service = CreateService();

        var act = () => service.TriggerAsync("missing");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Cron job 'missing' not found.");
    }

    [Fact]
    public async Task TickLoop_RespectsEnabledFlag()
    {
        var service = CreateService(tickSeconds: 1);
        var job = new TestCronJob("job-a", "* * * * * *");
        service.Register(job);
        service.SetEnabled("job-a", false);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(1300);
            job.ExecutionCount.Should().Be(0);

            service.SetEnabled("job-a", true);
            await WaitForAsync(() => job.ExecutionCount > 0);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_DoesNotRunLoop()
    {
        var service = CreateService(enabled: false);
        var job = new TestCronJob("job-a", "* * * * * *");
        service.Register(job);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await service.StopAsync(CancellationToken.None);

        service.IsRunning.Should().BeFalse();
        job.ExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task GetHistory_RespectsConfiguredHistorySize()
    {
        var service = CreateService(historySize: 2);
        var job = new TestCronJob("job-a", "* * * * * *");
        service.Register(job);

        await service.TriggerAsync("job-a");
        await service.TriggerAsync("job-a");
        await service.TriggerAsync("job-a");
        await WaitForAsync(() => service.GetHistory("job-a", 10).Count == 2);

        var history = service.GetHistory("job-a", 10);
        history.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReloadFromConfigAsync_ReconcilesAddedUpdatedAndRemovedJobs()
    {
        var initialCronConfig = new CronConfig
        {
            Jobs = new Dictionary<string, CronJobConfig>
            {
                ["alpha"] = new()
                {
                    Name = "alpha",
                    Type = "system",
                    Action = "check-updates",
                    Schedule = "0 */5 * * *"
                }
            }
        };

        var currentBotConfig = new BotNexusConfig { Cron = initialCronConfig };
        var cronConfigMonitor = new MutableOptionsMonitor<CronConfig>(initialCronConfig);
        var botConfigMonitor = new MutableOptionsMonitor<BotNexusConfig>(currentBotConfig);

        var services = new ServiceCollection();
        services.AddSingleton<ISystemActionRegistry>(new SystemActionRegistry());
        services.AddSingleton(Mock.Of<IMemoryConsolidator>());
        services.AddSingleton(Mock.Of<ISessionManager>());
        var serviceProvider = services.BuildServiceProvider();
        var factory = new CronJobFactory(cronConfigMonitor, botConfigMonitor, serviceProvider, NullLogger<CronJobFactory>.Instance);
        var service = new CronService(
            NullLogger<CronService>.Instance,
            serviceProvider,
            new TestActivityStream(),
            Mock.Of<IBotNexusMetrics>(),
            botConfigMonitor,
            factory);

        await service.ReloadFromConfigAsync();
        service.GetJobs().Select(j => j.Name).Should().Contain("system:check-updates");

        var updatedCronConfig = new CronConfig
        {
            Jobs = new Dictionary<string, CronJobConfig>
            {
                ["alpha"] = new()
                {
                    Name = "alpha",
                    Type = "system",
                    Action = "check-updates",
                    Schedule = "*/1 * * * *"
                },
                ["beta"] = new()
                {
                    Name = "beta",
                    Type = "maintenance",
                    Action = "cleanup-sessions",
                    Schedule = "0 2 * * *"
                }
            }
        };

        cronConfigMonitor.Update(updatedCronConfig);
        botConfigMonitor.Update(new BotNexusConfig { Cron = updatedCronConfig });
        await service.ReloadFromConfigAsync();

        var jobs = service.GetJobs();
        jobs.Select(j => j.Name).Should().BeEquivalentTo(["system:check-updates", "maintenance:cleanup-sessions"]);
        jobs.Should().ContainSingle(j => j.Name == "system:check-updates" && j.Schedule == "*/1 * * * *");
    }

    private static CronService CreateService(
        bool enabled = true,
        int tickSeconds = 10,
        int historySize = 100,
        IActivityStream? activityStream = null)
    {
        var config = new BotNexusConfig
        {
            Cron = new CronConfig
            {
                Enabled = enabled,
                TickIntervalSeconds = tickSeconds,
                ExecutionHistorySize = historySize
            }
        };

        return new CronService(
            NullLogger<CronService>.Instance,
            new ServiceCollection().BuildServiceProvider(),
            activityStream ?? new TestActivityStream(),
            Mock.Of<IBotNexusMetrics>(),
            new MutableOptionsMonitor<BotNexusConfig>(config));
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var started = DateTime.UtcNow;
        while (!condition())
        {
            if ((DateTime.UtcNow - started).TotalMilliseconds > timeoutMs)
                throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(25);
        }
    }

    private sealed class TestCronJob(string name, string schedule) : ICronJob
    {
        public int ExecutionCount { get; private set; }
        public Func<CronJobContext, CancellationToken, Task<CronJobResult>>? ExecuteHandler { get; init; }

        public string Name { get; } = name;
        public CronJobType Type => CronJobType.Agent;
        public string Schedule { get; } = schedule;
        public TimeZoneInfo? TimeZone => null;
        public bool Enabled { get; set; } = true;

        public async Task<CronJobResult> ExecuteAsync(CronJobContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            if (ExecuteHandler is not null)
                return await ExecuteHandler(context, cancellationToken);

            return new CronJobResult(true, "ok", Duration: TimeSpan.FromMilliseconds(1));
        }
    }

    private sealed class TestActivityStream : IActivityStream
    {
        public List<ActivityEvent> Events { get; } = [];

        public ValueTask PublishAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(activityEvent);
            return ValueTask.CompletedTask;
        }

        public IActivitySubscription Subscribe() => throw new NotSupportedException();
    }

    private sealed class MutableOptionsMonitor<T>(T initialValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; private set; } = initialValue;

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        public void Update(T newValue) => CurrentValue = newValue;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}

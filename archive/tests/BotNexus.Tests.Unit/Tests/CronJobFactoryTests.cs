using BotNexus.Channels.Base;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Extensions;
using BotNexus.Core.Models;
using BotNexus.Cron;
using BotNexus.Cron.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Tests.Unit.Tests;

public sealed class CronJobFactoryTests
{
    [Fact]
    public void CreateAndRegisterAll_RegistersSupportedJobTypes_AndSkipsUnknownTypes()
    {
        var cronConfig = new CronConfig
        {
            Jobs = new Dictionary<string, CronJobConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["agent-digest"] = new()
                {
                    Type = "agent",
                    Schedule = "0 9 * * *",
                    Agent = "bender",
                    Prompt = "Summarize activity."
                },
                ["system-health"] = new()
                {
                    Type = "system",
                    Schedule = "*/15 * * * *",
                    Action = "health-audit"
                },
                ["maintenance-cleanup"] = new()
                {
                    Type = "maintenance",
                    Schedule = "0 2 * * *",
                    Action = "cleanup-sessions"
                },
                ["bad-type"] = new()
                {
                    Type = "nonesuch",
                    Schedule = "* * * * *"
                }
            }
        };

        var serviceProvider = BuildServiceProvider();
        var factory = new CronJobFactory(
            new TestOptionsMonitor<CronConfig>(cronConfig),
            new TestOptionsMonitor<BotNexusConfig>(new BotNexusConfig()),
            serviceProvider,
            NullLogger<CronJobFactory>.Instance);
        var cronService = new Mock<ICronService>();

        factory.CreateAndRegisterAll(cronService.Object);

        cronService.Verify(x => x.Register(It.IsAny<AgentCronJob>()), Times.Once);
        cronService.Verify(x => x.Register(It.IsAny<SystemCronJob>()), Times.Once);
        cronService.Verify(x => x.Register(It.IsAny<MaintenanceCronJob>()), Times.Once);
        cronService.Verify(x => x.Register(It.IsAny<ICronJob>()), Times.Exactly(3));
    }

    [Fact]
    public void CreateAndRegisterAll_InvalidJobConfig_DoesNotThrowAndSkipsInvalidJob()
    {
        var cronConfig = new CronConfig
        {
            Jobs = new Dictionary<string, CronJobConfig>
            {
                ["invalid-agent"] = new()
                {
                    Type = "agent",
                    Schedule = "0 8 * * *",
                    Agent = "bender"
                }
            }
        };

        var serviceProvider = BuildServiceProvider();
        var factory = new CronJobFactory(
            new TestOptionsMonitor<CronConfig>(cronConfig),
            new TestOptionsMonitor<BotNexusConfig>(new BotNexusConfig()),
            serviceProvider,
            NullLogger<CronJobFactory>.Instance);
        var cronService = new Mock<ICronService>();

        var act = () => factory.CreateAndRegisterAll(cronService.Object);

        act.Should().NotThrow();
        cronService.Verify(x => x.Register(It.IsAny<ICronJob>()), Times.Never);
    }

    [Fact]
    public void CreateAndRegisterAll_MigratesLegacyAgentCronJobsIntoCentralizedRegistration()
    {
        var cronConfig = new CronConfig();
        var botNexusConfig = new BotNexusConfig
        {
            Agents = new AgentDefaults
            {
                Named = new Dictionary<string, AgentConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["farnsworth"] = new()
                    {
#pragma warning disable CS0618
                        CronJobs =
                        [
                            new CronJobConfig
                            {
                                Type = "agent",
                                Schedule = "0 7 * * *",
                                Prompt = "Daily platform check."
                            }
                        ]
#pragma warning restore CS0618
                    }
                }
            }
        };

        var serviceProvider = BuildServiceProvider();
        var factory = new CronJobFactory(
            new TestOptionsMonitor<CronConfig>(cronConfig),
            new TestOptionsMonitor<BotNexusConfig>(botNexusConfig),
            serviceProvider,
            NullLogger<CronJobFactory>.Instance);
        var cronService = new Mock<ICronService>();
        ICronJob? registeredJob = null;
        cronService
            .Setup(x => x.Register(It.IsAny<ICronJob>()))
            .Callback<ICronJob>(job => registeredJob = job);

        factory.CreateAndRegisterAll(cronService.Object);

        cronService.Verify(x => x.Register(It.IsAny<AgentCronJob>()), Times.Once);
        registeredJob.Should().NotBeNull();
        registeredJob!.Name.Should().Be("farnsworth");
    }

    [Fact]
    public void CreateAndRegisterAll_HandlesCaseInsensitiveTypeAndNameOverride()
    {
        var cronConfig = new CronConfig
        {
            Jobs = new Dictionary<string, CronJobConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["agent-digest"] = new()
                {
                    Name = "digest-job",
                    Type = "AGENT",
                    Schedule = "0 9 * * *",
                    Agent = "bender",
                    Prompt = "Summarize activity."
                }
            }
        };

        var serviceProvider = BuildServiceProvider();
        var factory = new CronJobFactory(
            new TestOptionsMonitor<CronConfig>(cronConfig),
            new TestOptionsMonitor<BotNexusConfig>(new BotNexusConfig()),
            serviceProvider,
            NullLogger<CronJobFactory>.Instance);
        var cronService = new Mock<ICronService>();
        ICronJob? registeredJob = null;
        cronService
            .Setup(x => x.Register(It.IsAny<ICronJob>()))
            .Callback<ICronJob>(job => registeredJob = job);

        factory.CreateAndRegisterAll(cronService.Object);

        cronService.Verify(x => x.Register(It.IsAny<ICronJob>()), Times.Once);
        registeredJob.Should().NotBeNull();
        registeredJob!.Name.Should().Be("digest-job");
    }

    [Fact]
    public void CreateAndRegisterAll_InvalidCronSchedule_StillRegistersJob()
    {
        var cronConfig = new CronConfig
        {
            Jobs = new Dictionary<string, CronJobConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["broken"] = new()
                {
                    Type = "agent",
                    Schedule = "not-a-cron",
                    Agent = "bender",
                    Prompt = "Run"
                }
            }
        };

        var serviceProvider = BuildServiceProvider();
        var factory = new CronJobFactory(
            new TestOptionsMonitor<CronConfig>(cronConfig),
            new TestOptionsMonitor<BotNexusConfig>(new BotNexusConfig()),
            serviceProvider,
            NullLogger<CronJobFactory>.Instance);
        var cronService = new Mock<ICronService>();

        factory.CreateAndRegisterAll(cronService.Object);
        cronService.Verify(x => x.Register(It.IsAny<ICronJob>()), Times.Once);
    }

    [Fact]
    public void CreateAndRegisterAll_MigratesLegacyJobsWithUniqueSuffixWhenKeyAlreadyExists()
    {
        var cronConfig = new CronConfig
        {
            Jobs = new Dictionary<string, CronJobConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["farnsworth-legacy-1"] = new()
                {
                    Type = "system",
                    Schedule = "* * * * *",
                    Action = "check-updates"
                }
            }
        };

        var botNexusConfig = new BotNexusConfig
        {
            Agents = new AgentDefaults
            {
                Named = new Dictionary<string, AgentConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["farnsworth"] = new()
                    {
#pragma warning disable CS0618
                        CronJobs = [new CronJobConfig
                        {
                            Type = "agent",
                            Schedule = "0 7 * * *",
                            Prompt = "Daily platform check."
                        }]
#pragma warning restore CS0618
                    }
                }
            }
        };

        var serviceProvider = BuildServiceProvider();
        var factory = new CronJobFactory(
            new TestOptionsMonitor<CronConfig>(cronConfig),
            new TestOptionsMonitor<BotNexusConfig>(botNexusConfig),
            serviceProvider,
            NullLogger<CronJobFactory>.Instance);
        var cronService = new Mock<ICronService>();
        var registered = new List<ICronJob>();
        cronService
            .Setup(x => x.Register(It.IsAny<ICronJob>()))
            .Callback<ICronJob>(registered.Add);

        factory.CreateAndRegisterAll(cronService.Object);

        registered.Should().HaveCount(2);
        registered.Should().Contain(job => job.Name == "farnsworth");
        registered.Should().Contain(job => job.Name.StartsWith("system:", StringComparison.OrdinalIgnoreCase));
    }

    private static IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IAgentRunnerFactory>());
        services.AddSingleton(Mock.Of<ISessionManager>());
        services.AddSingleton(Mock.Of<IMemoryConsolidator>());
        services.AddSingleton<ISystemActionRegistry>(new SystemActionRegistry());
        services.AddSingleton<IChannel>(new TestChannel("websocket"));
        return services.BuildServiceProvider();
    }

    private sealed class TestChannel(string name) : IChannel
    {
        public string Name { get; } = name;
        public string DisplayName => Name;
        public bool IsRunning => true;
        public bool SupportsStreaming => false;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendDeltaAsync(string chatId, string delta, IReadOnlyDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool IsAllowed(string senderId) => true;
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; private set; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}

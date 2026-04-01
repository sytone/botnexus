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
            Options.Create(cronConfig),
            Options.Create(new BotNexusConfig()),
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
            Options.Create(cronConfig),
            Options.Create(new BotNexusConfig()),
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
            Options.Create(cronConfig),
            Options.Create(botNexusConfig),
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
        public string DisplayName => name;
        public bool IsRunning => true;
        public bool SupportsStreaming => false;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendDeltaAsync(string chatId, string delta, IReadOnlyDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool IsAllowed(string senderId) => true;
    }
}

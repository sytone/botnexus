using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Extensions;
using BotNexus.Core.Models;
using BotNexus.Cron.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Tests.Unit.Tests;

public sealed class SystemCronJobTests
{
    [Fact]
    public async Task ExecuteAsync_RunsActionAndRoutesOutput()
    {
        var action = new TestSystemAction("check-updates", "ok");
        var registry = new SystemActionRegistry([action]);
        var config = new CronJobConfig
        {
            Schedule = "* * * * *",
            Action = "check-updates",
            OutputChannels = ["websocket"]
        };

        var channel = new TestChannel("websocket");
        var services = new ServiceCollection()
            .AddSingleton<IChannel>(channel)
            .BuildServiceProvider();
        var context = BuildContext(services, "system-check");
        var job = new SystemCronJob(config, registry);

        var result = await job.ExecuteAsync(context, CancellationToken.None);

        result.Success.Should().BeTrue();
        action.ExecuteCount.Should().Be(1);
        channel.Messages.Should().ContainSingle()
            .Which.Content.Should().Be("ok");
        result.Metadata.Should().NotBeNull();
        result.Metadata!["routedChannels"].Should().Be(1);
    }

    private static CronJobContext BuildContext(IServiceProvider services, string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new CronJobContext
        {
            JobName = name,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ScheduledTime = now,
            ActualTime = now,
            Services = services
        };
    }

    private sealed class TestSystemAction(string name, string result) : ISystemAction
    {
        public int ExecuteCount { get; private set; }
        public string Name { get; } = name;
        public string Description => "test action";

        public Task<string> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class TestChannel : IChannel
    {
        public TestChannel(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public string DisplayName => Name;
        public bool IsRunning => true;
        public bool SupportsStreaming => false;
        public List<OutboundMessage> Messages { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task SendDeltaAsync(string chatId, string delta, IReadOnlyDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public bool IsAllowed(string senderId) => true;
    }
}

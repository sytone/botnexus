using BotNexus.Cron.Actions;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BotNexus.Cron.Tests;

public sealed class AgentPromptActionTests
{
    [Fact]
    public async Task ExecuteAsync_DispatchesMessageToGateway()
    {
        var action = new AgentPromptAction();
        var dispatcher = new Mock<IChannelDispatcher>();
        InboundMessage? captured = null;

        dispatcher.Setup(value => value.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboundMessage, CancellationToken>((message, _) => captured = message)
            .Returns(Task.CompletedTask);

        var services = BuildServices(dispatcher.Object);
        var context = CreateContext(services);

        await action.ExecuteAsync(context);

        captured.Should().NotBeNull();
        captured!.TargetAgentId.Should().Be("agent-a");
        captured.Content.Should().Be("Ping from cron");
        captured.Metadata.Should().ContainKey("jobId");
        dispatcher.Verify(value => value.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_CreatesSessionWithCronChannelType()
    {
        var action = new AgentPromptAction();
        var dispatcher = new Mock<IChannelDispatcher>();
        InboundMessage? captured = null;

        dispatcher.Setup(value => value.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboundMessage, CancellationToken>((message, _) => captured = message)
            .Returns(Task.CompletedTask);

        var services = BuildServices(dispatcher.Object);
        var context = CreateContext(services);

        await action.ExecuteAsync(context);

        captured.Should().NotBeNull();
        captured!.ChannelType.Should().Be(ChannelKey.From(AgentPromptAction.CronChannelType));
        context.SessionId.Should().NotBeNull();
        context.SessionId.Should().StartWith("cron:job-1:");
    }

    private static IServiceProvider BuildServices(IChannelDispatcher dispatcher)
        => new ServiceCollection()
            .AddSingleton(dispatcher)
            .BuildServiceProvider();

    private static CronExecutionContext CreateContext(IServiceProvider services)
        => new()
        {
            Job = new CronJob
            {
                Id = "job-1",
                Name = "Cron prompt",
                Schedule = "*/1 * * * *",
                ActionType = "agent-prompt",
                AgentId = "agent-a",
                Message = "Ping from cron",
                CreatedBy = "tester",
                CreatedAt = DateTimeOffset.UtcNow,
                Enabled = true
            },
            RunId = "run-1",
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Scheduled,
            Services = services
        };
}

using BotNexus.Cron.Actions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Triggers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BotNexus.Cron.Tests;

public sealed class QuietHoursTests
{
    [Fact]
    public async Task ExecuteAsync_DuringQuietHours_SkipsExecution()
    {
        var action = new AgentPromptAction();
        var trigger = new Mock<IInternalTrigger>();
        var registry = new Mock<IAgentRegistry>();
        var descriptor = CreateDescriptor(new QuietHoursConfig
        {
            Enabled = true,
            Start = "00:00",
            End = "23:59",
            Timezone = "UTC"
        });

        trigger.SetupGet(value => value.Type).Returns(TriggerType.Cron);
        registry.Setup(value => value.Get(AgentId.From("agent-a"))).Returns(descriptor);

        var services = new ServiceCollection()
            .AddSingleton(trigger.Object)
            .AddSingleton(registry.Object)
            .BuildServiceProvider();
        var context = CreateHeartbeatContext(services);

        await action.ExecuteAsync(context);

        trigger.Verify(value => value.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        context.SessionId.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_OutsideQuietHours_ExecutesNormally()
    {
        var action = new AgentPromptAction();
        var trigger = new Mock<IInternalTrigger>();
        var registry = new Mock<IAgentRegistry>();
        var descriptor = CreateDescriptor(new QuietHoursConfig
        {
            Enabled = true,
            Start = "00:00",
            End = "00:00",
            Timezone = "UTC"
        });
        var sessionId = SessionId.From("cron:heartbeat:run-1");

        trigger.SetupGet(value => value.Type).Returns(TriggerType.Cron);
        trigger.Setup(value => value.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sessionId);
        registry.Setup(value => value.Get(AgentId.From("agent-a"))).Returns(descriptor);

        var services = new ServiceCollection()
            .AddSingleton(trigger.Object)
            .AddSingleton(registry.Object)
            .BuildServiceProvider();
        var context = CreateHeartbeatContext(services);

        await action.ExecuteAsync(context);

        trigger.Verify(value => value.CreateSessionAsync(AgentId.From("agent-a"), "Ping from heartbeat", It.IsAny<CancellationToken>()), Times.Once);
        context.SessionId.Should().Be(sessionId.Value);
    }

    private static CronExecutionContext CreateHeartbeatContext(IServiceProvider services)
        => new()
        {
            Job = new CronJob
            {
                Id = "heartbeat:agent-a",
                Name = "Heartbeat",
                Schedule = "*/30 * * * *",
                ActionType = "agent-prompt",
                AgentId = "agent-a",
                Message = "Ping from heartbeat",
                Enabled = true,
                System = true,
                CreatedBy = "system:heartbeat",
                CreatedAt = DateTimeOffset.UtcNow
            },
            RunId = "run-1",
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Scheduled,
            Services = services
        };

    private static AgentDescriptor CreateDescriptor(QuietHoursConfig quietHours)
        => new()
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            Heartbeat = new HeartbeatAgentConfig
            {
                Enabled = true,
                QuietHours = quietHours
            }
        };
}

using BotNexus.Cron.Actions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Domain.Primitives;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BotNexus.Cron.Tests;

public sealed class AgentPromptActionTests
{
    [Fact]
    public async Task ExecuteAsync_CreatesSessionUsingCronTrigger()
    {
        var action = new AgentPromptAction();
        var trigger = new Mock<IInternalTrigger>();
        AgentId capturedAgentId = default;
        string? capturedPrompt = null;
        var createdSession = SessionId.From("cron:job-1:run-1");

        trigger.SetupGet(value => value.Type).Returns(TriggerType.Cron);
        trigger.Setup(value => value.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<AgentId, string, CancellationToken>((agentId, prompt, _) =>
            {
                capturedAgentId = agentId;
                capturedPrompt = prompt;
            })
            .ReturnsAsync(createdSession);

        var services = BuildServices(trigger.Object);
        var context = CreateContext(services);

        await action.ExecuteAsync(context);

        capturedAgentId.Should().Be(AgentId.From("agent-a"));
        capturedPrompt.Should().Be("Ping from cron");
        context.SessionId.Should().Be(createdSession.Value);
        trigger.Verify(value => value.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsWhenCronTriggerMissing()
    {
        var action = new AgentPromptAction();
        var services = new ServiceCollection().BuildServiceProvider();
        var context = CreateContext(services);

        var act = () => action.ExecuteAsync(context);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cron internal trigger is not registered*");
    }

    private static IServiceProvider BuildServices(IInternalTrigger trigger)
        => new ServiceCollection()
            .AddSingleton<IInternalTrigger>(trigger)
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

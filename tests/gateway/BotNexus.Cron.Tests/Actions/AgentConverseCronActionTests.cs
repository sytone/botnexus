using BotNexus.Cron;
using BotNexus.Cron.Actions;
using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotNexus.Cron.Tests.Actions;

public sealed class AgentConverseCronActionTests
{
    private readonly AgentConverseCronAction _action = new();

    [Fact]
    public void ActionType_ReturnsAgentConverse()
    {
        _action.ActionType.ShouldBe("agent-converse");
    }

    [Fact]
    public async Task Execute_WithValidConfig_CallsConverseAsync()
    {
        var mockExchange = new Mock<IAgentExchangeService>();
        mockExchange.Setup(x => x.ConverseAsync(It.IsAny<AgentExchangeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentExchangeResult { SessionId = SessionId.From("s1"), ConversationId = ConversationId.From("c1"), Status = "completed", Turns = 1, FinalResponse = "Done" });

        var services = new ServiceCollection()
            .AddSingleton(mockExchange.Object)
            .AddSingleton<ILogger<AgentConverseCronAction>>(new Mock<ILogger<AgentConverseCronAction>>().Object)
            .BuildServiceProvider();

        var job = CreateJob("test-agent", metadata: new Dictionary<string, object?>
        {
            ["targetAgentId"] = "target-agent",
            ["message"] = "Hello!",
            ["objective"] = "Daily sync",
            ["maxTurns"] = "3"
        });

        var context = CreateContext(job, services);
        await _action.ExecuteAsync(context);

        mockExchange.Verify(x => x.ConverseAsync(
            It.Is<AgentExchangeRequest>(r =>
                r.InitiatorId == AgentId.From("test-agent") &&
                r.TargetId == AgentId.From("target-agent") &&
                r.Message == "Hello!" &&
                r.Objective == "Daily sync" &&
                r.MaxTurns == 3),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_FallsBackToJobMessage_WhenMetadataMessageMissing()
    {
        var mockExchange = new Mock<IAgentExchangeService>();
        mockExchange.Setup(x => x.ConverseAsync(It.IsAny<AgentExchangeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentExchangeResult { SessionId = SessionId.From("s1"), ConversationId = ConversationId.From("c1"), Status = "completed", Turns = 1, FinalResponse = "Done" });

        var services = new ServiceCollection()
            .AddSingleton(mockExchange.Object)
            .BuildServiceProvider();

        var job = CreateJob("initiator", message: "Fallback message", metadata: new Dictionary<string, object?>
        {
            ["targetAgentId"] = "target"
        });

        var context = CreateContext(job, services);
        await _action.ExecuteAsync(context);

        mockExchange.Verify(x => x.ConverseAsync(
            It.Is<AgentExchangeRequest>(r => r.Message == "Fallback message"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_UsesDefaultMaxTurns_WhenNotSpecified()
    {
        var mockExchange = new Mock<IAgentExchangeService>();
        mockExchange.Setup(x => x.ConverseAsync(It.IsAny<AgentExchangeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentExchangeResult { SessionId = SessionId.From("s1"), ConversationId = ConversationId.From("c1"), Status = "completed", Turns = 1, FinalResponse = "Done" });

        var services = new ServiceCollection()
            .AddSingleton(mockExchange.Object)
            .BuildServiceProvider();

        var job = CreateJob("initiator", message: "Hi", metadata: new Dictionary<string, object?>
        {
            ["targetAgentId"] = "target"
        });

        var context = CreateContext(job, services);
        await _action.ExecuteAsync(context);

        mockExchange.Verify(x => x.ConverseAsync(
            It.Is<AgentExchangeRequest>(r => r.MaxTurns == AgentConverseCronAction.DefaultMaxTurns),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_ThrowsWhenAgentIdMissing()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var job = CreateJob(null, metadata: new Dictionary<string, object?> { ["targetAgentId"] = "t" });
        var context = CreateContext(job, services);

        await Should.ThrowAsync<InvalidOperationException>(() => _action.ExecuteAsync(context));
    }

    [Fact]
    public async Task Execute_ThrowsWhenTargetAgentIdMissing()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var job = CreateJob("initiator", message: "Hi", metadata: new Dictionary<string, object?>());
        var context = CreateContext(job, services);

        await Should.ThrowAsync<InvalidOperationException>(() => _action.ExecuteAsync(context));
    }

    [Fact]
    public async Task Execute_ThrowsWhenNoMessageAvailable()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var job = CreateJob("initiator", metadata: new Dictionary<string, object?> { ["targetAgentId"] = "t" });
        var context = CreateContext(job, services);

        await Should.ThrowAsync<InvalidOperationException>(() => _action.ExecuteAsync(context));
    }

    [Fact]
    public async Task Execute_ThrowsWhenExchangeServiceNotRegistered()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var job = CreateJob("initiator", message: "Hi", metadata: new Dictionary<string, object?>
        {
            ["targetAgentId"] = "target"
        });
        var context = CreateContext(job, services);

        await Should.ThrowAsync<InvalidOperationException>(() => _action.ExecuteAsync(context));
    }

    private static CronJob CreateJob(string? agentId, string? message = null, Dictionary<string, object?>? metadata = null)
        => new()
        {
            Id = JobId.From("test-job-1"),
            Name = "Test Converse Job",
            AgentId = agentId is null ? null : AgentId.From(agentId),
            Schedule = "0 9 * * 1-5",
            ActionType = "agent-converse",
            Message = message,
            Metadata = metadata
        };

    private static CronExecutionContext CreateContext(CronJob job, IServiceProvider services)
        => new()
        {
            Job = job,
            RunId = RunId.From("run-1"),
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Manual,
            Services = services
        };
}
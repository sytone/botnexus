using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Api.Triggers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Api;

public sealed class CronTriggerTests
{
    [Fact]
    public void CronTrigger_ImplementsInternalTrigger_NotChannelAdapter()
    {
        typeof(IInternalTrigger).IsAssignableFrom(typeof(CronTrigger)).ShouldBeTrue();
        typeof(IChannelAdapter).IsAssignableFrom(typeof(CronTrigger)).ShouldBeFalse();
    }

    [Fact]
    public async Task CreateSessionAsync_CreatesCronSession_WithCronTriggerType()
    {
        var sessionStore = new Mock<ISessionStore>();
        var supervisor = new Mock<IAgentSupervisor>();
        var handle = new Mock<IAgentHandle>();
        GatewaySession? savedSession = null;

        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-a"));
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "cron-response" });

        sessionStore
            .Setup(s => s.GetOrCreateAsync(It.IsAny<SessionId>(), AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .Returns<SessionId, AgentId, CancellationToken>((sessionId, agentId, _) => Task.FromResult(new GatewaySession
            {
                SessionId = sessionId,
                AgentId = agentId
            }));
        sessionStore
            .Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((session, _) => savedSession = session)
            .Returns(Task.CompletedTask);
        supervisor
            .Setup(s => s.GetOrCreateAsync(AgentId.From("agent-a"), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var trigger = new CronTrigger(supervisor.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);
        var sessionId = await trigger.CreateSessionAsync(AgentId.From("agent-a"), "Run scheduled task");

        trigger.Type.ShouldBe(TriggerType.Cron);
        sessionId.Value.ShouldStartWith("cron:");
        savedSession.ShouldNotBeNull();
        savedSession!.SessionType.ShouldBe(SessionType.Cron);
        savedSession.ChannelType.ShouldBe(ChannelKey.From("cron"));
        savedSession.CallerId.ShouldBe("cron:agent-a");
        savedSession.History.Where(e => e.Role == MessageRole.User && e.Content == "Run scheduled task").ShouldHaveSingleItem();
        savedSession.History.Where(e => e.Role == MessageRole.Assistant && e.Content == "cron-response").ShouldHaveSingleItem();

        supervisor.Verify(
            s => s.GetOrCreateAsync(AgentId.From("agent-a"), sessionId, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}

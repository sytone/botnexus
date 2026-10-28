using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Api.Triggers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Api;

public sealed class HeartbeatCronRoutingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateSessionAsync_CronRouting_IsSavedBeforeSupervisorAndRefreshes(bool soul)
    {
        var agentId = AgentId.From("heartbeat-test");
        var sessionId = SessionId.From("heartbeat-test-soul");
        var session = new GatewaySession { AgentId = agentId, SessionId = sessionId, Status = SessionStatus.Active };
        session.Metadata["soulDate"] = "2026-10-14";
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(agentId)).Returns(new AgentDescriptor
        {
            AgentId = agentId, DisplayName = "Test", ApiProvider = "p", ModelId = "first",
            Soul = soul ? new SoulAgentConfig { Enabled = true } : null
        });
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.ListAsync(agentId, It.IsAny<CancellationToken>())).ReturnsAsync([session]);
        sessions.Setup(s => s.GetOrCreateAsync(It.IsAny<SessionId>(), agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionId id, AgentId _, CancellationToken _) => id == sessionId ? session : new GatewaySession { AgentId = agentId, SessionId = id });
        Dictionary<string, object?>? saved = null;
        sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((s, _) => saved = new(s.Metadata)).Returns(Task.CompletedTask);
        var conversations = new Mock<IConversationStore>();
        conversations.Setup(c => c.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Conversation c, CancellationToken _) => c);
        conversations.Setup(c => c.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentResponse { Content = "HEARTBEAT_OK" });
        var supervisor = new Mock<IAgentSupervisor>();
        var expected = "first";
        var calls = 0;
        supervisor.Setup(s => s.GetOrCreateAsync(agentId, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                saved.ShouldNotBeNull();
                saved.ShouldContainKey("cronModelOverride");
                saved["cronModelOverride"].ShouldBe($"p/{expected}");
                saved["modelOverride"].ShouldBe($"p/{expected}");
                saved["cronRequestedModel"].ShouldBe($"p/{expected}");
                saved["cronModelSource"].ShouldBe("global");
                saved["cronEffectiveProvider"].ShouldBe("p");
                saved["cronEffectiveModel"].ShouldBe(expected);
                saved["cronJobId"].ShouldBe("heartbeat-job");
                calls++;
            }).ReturnsAsync(handle.Object);
        var trigger = new HeartbeatTrigger(supervisor.Object, registry.Object, conversations.Object, sessions.Object, NullLogger<HeartbeatTrigger>.Instance);
        foreach (var model in new[] { "first", "second" })
        {
            expected = model;
            await trigger.CreateSessionAsync(agentId, "ping", request: new InternalTriggerRequest
            {
                CronJobId = JobId.From("heartbeat-job"), ModelOverride = $"p/{model}",
                CronRequestedModel = $"p/{model}", CronModelSource = "global"
            });
        }
        calls.ShouldBe(2);
        session.Metadata["soulDate"].ShouldBe("2026-10-14");
        if (soul)
        {
            session.Metadata.ShouldNotContainKey("cronModelOverride");
            session.Metadata.ShouldNotContainKey("modelOverride");
        }
    }
}

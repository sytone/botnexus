using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Api.Triggers;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Api;

public sealed class HeartbeatTriggerTests
{
    [Fact]
    public void Type_IsHeartbeat()
    {
        var trigger = BuildTrigger();
        trigger.Type.ShouldBe(TriggerType.Heartbeat);
    }

    [Fact]
    public void DisplayName_IsHeartbeat()
    {
        var trigger = BuildTrigger();
        trigger.DisplayName.ShouldBe("Heartbeat");
    }

    [Fact]
    public async Task CreateSessionAsync_NonSoulAgent_CreatesHeartbeatSession()
    {
        var agentId = AgentId.From("agent-a");
        var (trigger, mocks) = BuildTriggerWithMocks(soul: false);
        var captured = SetupPromptCapture(mocks, agentId);

        var sessionId = await trigger.CreateSessionAsync(agentId, "Heartbeat ping");

        sessionId.Value.ShouldStartWith("heartbeat:");
        captured.Prompt.ShouldBe("Heartbeat ping");
        mocks.Sessions.Verify(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task CreateSessionAsync_SoulAgent_WithActiveSoulSession_UsesIt()
    {
        var agentId = AgentId.From("agent-a");
        var soulSessionId = SessionId.From("agent-a::soul::2026-05-18");
        var (trigger, mocks) = BuildTriggerWithMocks(soul: true);

        var existingSoulSession = new GatewaySession { SessionId = soulSessionId, AgentId = agentId, Status = BotNexus.Gateway.Abstractions.Models.SessionStatus.Active, UpdatedAt = DateTimeOffset.UtcNow };
        existingSoulSession.SessionType = SessionType.Soul;

        mocks.Sessions.Setup(s => s.ListAsync(agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([existingSoulSession]);
        mocks.Sessions.Setup(s => s.GetOrCreateAsync(soulSessionId, agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingSoulSession);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "HEARTBEAT_OK" });
        mocks.Supervisor.Setup(s => s.GetOrCreateAsync(agentId, soulSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessionId = await trigger.CreateSessionAsync(agentId, "Heartbeat ping");

        sessionId.ShouldBe(soulSessionId);
    }

    [Fact]
    public async Task CreateSessionAsync_SoulAgent_NoActiveSoulSession_FallsBackToHeartbeatSession()
    {
        var agentId = AgentId.From("agent-a");
        var (trigger, mocks) = BuildTriggerWithMocks(soul: true);

        // No active soul session
        mocks.Sessions.Setup(s => s.ListAsync(agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        SetupPromptCapture(mocks, agentId);

        var sessionId = await trigger.CreateSessionAsync(agentId, "Heartbeat ping");

        // Should fall back and create a heartbeat: prefixed session
        sessionId.Value.ShouldStartWith("heartbeat:");
    }

    [Fact]
    public async Task CreateSessionAsync_RecordsUserAndAssistantEntries()
    {
        var agentId = AgentId.From("agent-a");
        var (trigger, mocks) = BuildTriggerWithMocks(soul: false);
        var captured = SetupPromptCapture(mocks, agentId);

        await trigger.CreateSessionAsync(agentId, "Heartbeat ping");

        // Session should have been saved at least twice (before and after prompt)
        mocks.Sessions.Verify(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        captured.Response.ShouldBe("HEARTBEAT_OK");
    }

    [Fact]
    public async Task CreateSessionAsync_PropagatesCronJobIdAndModel()
    {
        var agentId = AgentId.From("agent-a");
        var (trigger, mocks) = BuildTriggerWithMocks(soul: false);
        GatewaySession? savedSession = null;

        SetupPromptCapture(mocks, agentId);
        mocks.Sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((session, _) => savedSession = session)
            .Returns(Task.CompletedTask);

        await trigger.CreateSessionAsync(agentId, "Heartbeat ping",
            request: new InternalTriggerRequest
            {
                CronJobId = JobId.From("heartbeat:agent-a"),
                ModelOverride = "openai/gpt-4.1"
            });

        savedSession.ShouldNotBeNull();
        savedSession!.Metadata.ShouldContainKey("cronJobId");
        savedSession.Metadata["cronJobId"].ShouldBe("heartbeat:agent-a");
        savedSession.Metadata.ShouldContainKey("modelOverride");
        savedSession.Metadata["modelOverride"].ShouldBe("openai/gpt-4.1");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private sealed record TriggerMocks(
        Mock<IAgentSupervisor> Supervisor,
        Mock<IAgentRegistry> Registry,
        Mock<IConversationStore> Conversations,
        Mock<ISessionStore> Sessions);

    private static HeartbeatTrigger BuildTrigger()
        => BuildTriggerWithMocks(soul: false).Trigger;

    private static (HeartbeatTrigger Trigger, TriggerMocks Mocks) BuildTriggerWithMocks(bool soul)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        var registry = new Mock<IAgentRegistry>();
        var conversations = new Mock<IConversationStore>();
        var sessions = new Mock<ISessionStore>();

        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "test",
            ApiProvider = "test",
            Soul = soul ? new SoulAgentConfig { Enabled = true } : null
        };
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns(descriptor);

        // Default: no existing sessions
        sessions.Setup(s => s.ListAsync(It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // Default: GetOrCreate returns a new blank session
        sessions.Setup(s => s.GetOrCreateAsync(It.IsAny<SessionId>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionId sid, AgentId aid, CancellationToken _) => new GatewaySession { SessionId = sid, AgentId = aid });
        sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Default: no existing conversation
        conversations.Setup(c => c.GetAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Conversation?)null);
        conversations.Setup(c => c.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Conversation conv, CancellationToken _) => conv);
        conversations.Setup(c => c.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mocks = new TriggerMocks(supervisor, registry, conversations, sessions);
        var trigger = new HeartbeatTrigger(
            supervisor.Object,
            registry.Object,
            conversations.Object,
            sessions.Object,
            NullLogger<HeartbeatTrigger>.Instance);

        return (trigger, mocks);
    }

    private sealed class PromptCapture
    {
        public string Prompt { get; set; } = string.Empty;
        public string Response { get; set; } = string.Empty;
    }

    private static PromptCapture SetupPromptCapture(TriggerMocks mocks, AgentId agentId)
    {
        var captured = new PromptCapture();
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((prompt, _) => captured.Prompt = prompt)
            .ReturnsAsync(() =>
            {
                captured.Response = "HEARTBEAT_OK";
                return new AgentResponse { Content = "HEARTBEAT_OK" };
            });

        mocks.Supervisor.Setup(s => s.GetOrCreateAsync(agentId, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        return captured;
    }
}

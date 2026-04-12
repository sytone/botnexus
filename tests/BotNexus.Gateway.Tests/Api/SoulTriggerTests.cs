using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Hubs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests.Api;

public sealed class SoulTriggerTests
{
    [Fact]
    public async Task CreateSessionAsync_WithInvalidCalendarSettings_FallsBackToUtcDefaults()
    {
        var agentId = AgentId.From("agent-a");
        var now = new DateTimeOffset(2026, 1, 10, 0, 30, 0, TimeSpan.Zero);
        var expectedSessionId = SessionId.ForSoul(agentId, new DateOnly(2026, 1, 10));

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(agentId)).Returns(CreateDescriptor(agentId, new SoulAgentConfig
        {
            Timezone = "Invalid/Timezone",
            DayBoundary = "not-a-time"
        }));

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.ListAsync(agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        GatewaySession? savedSession = null;
        sessions.Setup(s => s.GetOrCreateAsync(expectedSessionId, agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession { SessionId = expectedSessionId, AgentId = agentId });
        sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((session, _) => savedSession = session)
            .Returns(Task.CompletedTask);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync("hello", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "response" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(agentId, expectedSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var trigger = new SoulTrigger(
            supervisor.Object,
            registry.Object,
            sessions.Object,
            NullLogger<SoulTrigger>.Instance,
            new FixedTimeProvider(now));

        var result = await trigger.CreateSessionAsync(agentId, "hello");

        result.Should().Be(expectedSessionId);
        savedSession.Should().NotBeNull();
        savedSession!.SessionType.Should().Be(SessionType.Soul);
        savedSession.CallerId.Should().Be("soul:agent-a");
        savedSession.Metadata["soulDate"].Should().Be("2026-01-10");
    }

    [Fact]
    public async Task CreateSessionAsync_SealsOlderSoulSessions_WithReflectionBeforeNewSession()
    {
        var agentId = AgentId.From("agent-a");
        var now = new DateTimeOffset(2026, 1, 10, 8, 0, 0, TimeSpan.Zero);
        var previousDate = new DateOnly(2026, 1, 9);
        var todayDate = new DateOnly(2026, 1, 10);
        var previousSessionId = SessionId.ForSoul(agentId, previousDate);
        var todaySessionId = SessionId.ForSoul(agentId, todayDate);

        var previousSession = new GatewaySession
        {
            SessionId = previousSessionId,
            AgentId = agentId,
            SessionType = SessionType.Soul,
            Status = GatewaySessionStatus.Active,
            Metadata = new Dictionary<string, object?> { ["soulDate"] = "2026-01-09" }
        };
        var todaySession = new GatewaySession
        {
            SessionId = todaySessionId,
            AgentId = agentId
        };

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(agentId)).Returns(CreateDescriptor(agentId, new SoulAgentConfig
        {
            Timezone = "UTC",
            DayBoundary = "06:00",
            ReflectionOnSeal = true,
            ReflectionPrompt = "Reflect on yesterday"
        }));

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.ListAsync(agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([previousSession]);
        sessions.Setup(s => s.GetOrCreateAsync(todaySessionId, agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(todaySession);
        sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var reflectionHandle = new Mock<IAgentHandle>();
        reflectionHandle.Setup(h => h.PromptAsync("Reflect on yesterday", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "Yesterday summary" });

        var todayHandle = new Mock<IAgentHandle>();
        todayHandle.Setup(h => h.PromptAsync("new soul prompt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "Today response" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(agentId, previousSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reflectionHandle.Object);
        supervisor.Setup(s => s.GetOrCreateAsync(agentId, todaySessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(todayHandle.Object);

        var trigger = new SoulTrigger(
            supervisor.Object,
            registry.Object,
            sessions.Object,
            NullLogger<SoulTrigger>.Instance,
            new FixedTimeProvider(now));

        var result = await trigger.CreateSessionAsync(agentId, "new soul prompt");

        result.Should().Be(todaySessionId);
        previousSession.Status.Should().Be(GatewaySessionStatus.Sealed);
        previousSession.History.Should().Contain(entry =>
            entry.Role == MessageRole.User && entry.Content == "Reflect on yesterday");
        previousSession.History.Should().Contain(entry =>
            entry.Role == MessageRole.Assistant && entry.Content == "Yesterday summary");

        todaySession.Metadata["soulDate"].Should().Be("2026-01-10");
        todaySession.History.Should().Contain(entry => entry.Role == MessageRole.User && entry.Content == "new soul prompt");
        todaySession.History.Should().Contain(entry => entry.Role == MessageRole.Assistant && entry.Content == "Today response");
        sessions.Verify(s => s.SaveAsync(previousSession, It.IsAny<CancellationToken>()), Times.Once);
        sessions.Verify(s => s.SaveAsync(todaySession, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static AgentDescriptor CreateDescriptor(AgentId agentId, SoulAgentConfig soul)
        => new()
        {
            AgentId = agentId,
            DisplayName = "Agent A",
            ModelId = "model-a",
            ApiProvider = "provider-a",
            Soul = soul
        };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Triggers;
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

        result.ShouldBe(expectedSessionId);
        savedSession.ShouldNotBeNull();
        // P9-E (#645): soul sessions now carry SessionType.AgentSelf; the Soul proxy
        // origin is stamped on the user entry's Trigger field instead.
        savedSession!.SessionType.ShouldBe(SessionType.AgentSelf);
        savedSession.GetHistorySnapshot()
            .Any(e => e.Role == MessageRole.User && e.Trigger == TriggerType.Soul)
            .ShouldBeTrue();
        savedSession.CallerId.ShouldBe("soul:agent-a");
        savedSession.Metadata["soulDate"].ShouldBe("2026-01-10");
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
            // P9-E (#645): soul sessions now carry SessionType.AgentSelf and are
            // discovered via Metadata["soulDate"] rather than the deleted SessionType.Soul.
            SessionType = SessionType.AgentSelf,
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

        result.ShouldBe(todaySessionId);
        previousSession.Status.ShouldBe(GatewaySessionStatus.Sealed);
        previousSession.History.ShouldContain(entry =>
            entry.Role == MessageRole.User && entry.Content == "Reflect on yesterday");
        previousSession.History.ShouldContain(entry =>
            entry.Role == MessageRole.Assistant && entry.Content == "Yesterday summary");

        todaySession.Metadata["soulDate"].ShouldBe("2026-01-10");
        todaySession.History.ShouldContain(entry => entry.Role == MessageRole.User && entry.Content == "new soul prompt");
        todaySession.History.ShouldContain(entry => entry.Role == MessageRole.Assistant && entry.Content == "Today response");
        sessions.Verify(s => s.SaveAsync(previousSession, It.IsAny<CancellationToken>()), Times.Once);
        sessions.Verify(s => s.SaveAsync(todaySession, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CreateSessionAsync_SameDay_ReusesSoulSessionId()
    {
        var agentId = AgentId.From("agent-a");
        var now = new DateTimeOffset(2026, 1, 10, 8, 0, 0, TimeSpan.Zero);
        var soulDate = new DateOnly(2026, 1, 10);
        var expectedSessionId = SessionId.ForSoul(agentId, soulDate);

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(agentId)).Returns(CreateDescriptor(agentId, new SoulAgentConfig
        {
            Timezone = "UTC",
            DayBoundary = "06:00"
        }));

        var session = new GatewaySession
        {
            SessionId = expectedSessionId,
            AgentId = agentId
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.ListAsync(agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        sessions.Setup(s => s.GetOrCreateAsync(expectedSessionId, agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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

        var first = await trigger.CreateSessionAsync(
            agentId,
            "first prompt",
            request: new BotNexus.Gateway.Abstractions.Triggers.InternalTriggerRequest
            {
                CronJobId = JobId.From("job-1"),
                ModelOverride = "openai/gpt-4.1"
            });
        var second = await trigger.CreateSessionAsync(agentId, "second prompt");

        first.ShouldBe(expectedSessionId);
        second.ShouldBe(expectedSessionId);
        session.Metadata["soulDate"].ShouldBe("2026-01-10");
        sessions.Verify(s => s.GetOrCreateAsync(expectedSessionId, agentId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CreateSessionAsync_DailyTurnWithToolCalls_PersistsOrderedToolRows()
    {
        var agentId = AgentId.From("agent-a");
        var now = new DateTimeOffset(2026, 1, 10, 0, 30, 0, TimeSpan.Zero);
        var expectedSessionId = SessionId.ForSoul(agentId, new DateOnly(2026, 1, 10));

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(agentId)).Returns(CreateDescriptor(agentId, new SoulAgentConfig()));

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.ListAsync(agentId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        GatewaySession? saved = null;
        sessions.Setup(s => s.GetOrCreateAsync(expectedSessionId, agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession { SessionId = expectedSessionId, AgentId = agentId });
        sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((session, _) => saved = session)
            .Returns(Task.CompletedTask);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync("reflect", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse
            {
                Content = "done",
                ToolCalls =
                [
                    new AgentToolCallInfo("call-1", "read", false, "{\"path\":\"a.txt\"}", "file body"),
                    new AgentToolCallInfo("call-2", "write", true, "{\"path\":\"b.txt\"}", "boom")
                ]
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(agentId, expectedSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var trigger = new SoulTrigger(
            supervisor.Object,
            registry.Object,
            sessions.Object,
            NullLogger<SoulTrigger>.Instance,
            new FixedTimeProvider(now));

        await trigger.CreateSessionAsync(agentId, "reflect");

        saved.ShouldNotBeNull();
        var history = saved!.GetHistorySnapshot();
        var toolRows = history.Where(e => e.Role == MessageRole.Tool).ToArray();
        toolRows.Length.ShouldBe(2);
        toolRows[0].ToolCallId.ShouldBe("call-1");
        toolRows[0].ToolName.ShouldBe("read");
        toolRows[0].ToolArgs.ShouldNotBeNull().ShouldContain("a.txt");
        toolRows[0].Content.ShouldBe("file body");
        toolRows[0].ToolIsError.ShouldBeFalse();
        toolRows[1].ToolCallId.ShouldBe("call-2");
        toolRows[1].ToolIsError.ShouldBeTrue();
        // Tool rows precede the assistant text, mirroring the streaming timeline.
        var assistantIndex = Array.FindIndex(history.ToArray(), e => e.Role == MessageRole.Assistant);
        var lastToolIndex = Array.FindLastIndex(history.ToArray(), e => e.Role == MessageRole.Tool);
        lastToolIndex.ShouldBeLessThan(assistantIndex);
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

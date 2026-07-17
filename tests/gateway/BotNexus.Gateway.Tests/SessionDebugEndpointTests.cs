using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for GET /api/sessions/{sessionId}/debug endpoint (Issue #767, Part of #749).
/// </summary>
public sealed class SessionDebugEndpointTests
{
    [Fact]
    public async Task GetDebug_UnknownSession_Returns404()
    {
        var store = new InMemorySessionStore();
        var controller = new SessionsController(store);

        var result = await controller.GetDebug("nonexistent", 0, 50, CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetDebug_KnownSession_Returns200WithCorrectShape()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s-debug-1"), AgentId.From("agent-a"));
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "Hello" });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "World" });
        await store.SaveAsync(session);

        var controller = new SessionsController(store);

        var result = await controller.GetDebug("s-debug-1", 0, 50, CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var json = System.Text.Json.JsonSerializer.Serialize(ok!.Value);
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("sessionId").GetString().ShouldBe("s-debug-1");
        root.GetProperty("agentId").GetString().ShouldBe("agent-a");
        root.GetProperty("messageCount").GetInt32().ShouldBe(2);

        // history shape
        var history = root.GetProperty("history");
        history.GetProperty("totalCount").GetInt32().ShouldBe(2);
        history.GetProperty("offset").GetInt32().ShouldBe(0);
        history.GetProperty("limit").GetInt32().ShouldBe(50);
        history.GetProperty("entries").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task GetDebug_HistoryPagination_ReturnsCorrectSlice()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s-paged"), AgentId.From("agent-b"));
        for (var i = 0; i < 10; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"msg-{i}" });
        await store.SaveAsync(session);

        var controller = new SessionsController(store);

        var result = await controller.GetDebug("s-paged", offset: 3, limit: 4, CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var json = System.Text.Json.JsonSerializer.Serialize(ok!.Value);
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var history = doc.RootElement.GetProperty("history");

        history.GetProperty("totalCount").GetInt32().ShouldBe(10);
        history.GetProperty("offset").GetInt32().ShouldBe(3);
        history.GetProperty("limit").GetInt32().ShouldBe(4);
        history.GetProperty("entries").GetArrayLength().ShouldBe(4);
    }

    [Fact]
    public async Task GetDebug_SystemPromptNull_WhenNotCaptured()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync(SessionId.From("s-noprompt"), AgentId.From("agent-c"));

        var controller = new SessionsController(store);

        var result = await controller.GetDebug("s-noprompt", 0, 50, CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var json = System.Text.Json.JsonSerializer.Serialize(ok!.Value);
        var doc = System.Text.Json.JsonDocument.Parse(json);

        // systemPrompt should be present as null (or absent) -- not an error
        doc.RootElement.TryGetProperty("systemPrompt", out var sp);
        sp.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task GetDebug_SystemPrompt_FromLiveHandle_WhenActive()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s-live"), AgentId.From("agent-live"));
        // Stamped field is deliberately different so we can prove the live handle wins.
        session.LastRenderedSystemPrompt = "STALE stamped prompt";
        session.LastRenderedSystemPromptAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        await store.SaveAsync(session);

        var supervisor = BuildSupervisorWithHandle(
            "agent-live", "s-live", new ContextDiagnostics { SystemPrompt = "LIVE rendered prompt" });

        var controller = new SessionsController(store, supervisor: supervisor);

        var result = await controller.GetDebug("s-live", 0, 50, CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var root = SerializeRoot(ok!.Value);
        root.GetProperty("systemPrompt").GetString().ShouldBe("LIVE rendered prompt");
        root.GetProperty("systemPromptCapturedAt").ValueKind.ShouldNotBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task GetDebug_SystemPrompt_FallsBackToStamped_WhenNoLiveHandle()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s-cold"), AgentId.From("agent-cold"));
        var stampedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        session.LastRenderedSystemPrompt = "stamped cold prompt";
        session.LastRenderedSystemPromptAt = stampedAt;
        await store.SaveAsync(session);

        // Supervisor present but has no live handle for this session (idle-evicted).
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.As<IAgentHandleInspector>()
            .Setup(s => s.GetContextDiagnostics())
            .Returns((ContextDiagnostics?)null);

        var controller = new SessionsController(store, supervisor: supervisor.Object);

        var result = await controller.GetDebug("s-cold", 0, 50, CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var root = SerializeRoot(ok!.Value);
        root.GetProperty("systemPrompt").GetString().ShouldBe("stamped cold prompt");
    }

    [Fact]
    public async Task GetDebug_SystemPrompt_FallsBackToStamped_WhenLivePromptEmpty()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s-empty"), AgentId.From("agent-empty"));
        session.LastRenderedSystemPrompt = "stamped fallback prompt";
        session.LastRenderedSystemPromptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await store.SaveAsync(session);

        // Live handle exists but reports an empty prompt — must not clobber the stamped value.
        var supervisor = BuildSupervisorWithHandle(
            "agent-empty", "s-empty", new ContextDiagnostics { SystemPrompt = "" });

        var controller = new SessionsController(store, supervisor: supervisor);

        var result = await controller.GetDebug("s-empty", 0, 50, CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var root = SerializeRoot(ok!.Value);
        root.GetProperty("systemPrompt").GetString().ShouldBe("stamped fallback prompt");
    }

    [Fact]
    public async Task GetDebug_ActiveCronSession_DistinguishesLiveExecutionFromStalePersistence()
    {
        var store = new InMemorySessionStore();
        var stale = await store.GetOrCreateAsync(SessionId.From("cron:stale"), AgentId.From("agent-cron"));
        stale.ChannelType = ChannelKey.From("cron");
        await store.SaveAsync(stale);

        var supervisor = new Mock<IAgentSupervisor>();
        var controller = new SessionsController(store, supervisor: supervisor.Object);

        var result = await controller.GetDebug("cron:stale", 0, 50, CancellationToken.None);
        var root = SerializeRoot(result.Result.ShouldBeOfType<OkObjectResult>()!.Value);

        root.GetProperty("isExecutionLive").GetBoolean().ShouldBeFalse();
        root.GetProperty("lifecycleDiagnostic").GetString().ShouldBe("stale-persisted-active");
    }

    [Fact]
    public async Task GetDebug_ActiveCronSession_WithRunningHandle_ReportsLiveExecution()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("cron:live"), AgentId.From("agent-cron"));
        session.ChannelType = ChannelKey.From("cron");
        await store.SaveAsync(session);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.IsRunning).Returns(true);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetHandle(AgentId.From("agent-cron"), SessionId.From("cron:live")))
            .Returns(handle.Object);
        var controller = new SessionsController(store, supervisor: supervisor.Object);

        var result = await controller.GetDebug("cron:live", 0, 50, CancellationToken.None);
        var root = SerializeRoot(result.Result.ShouldBeOfType<OkObjectResult>()!.Value);

        root.GetProperty("isExecutionLive").GetBoolean().ShouldBeTrue();
        root.GetProperty("lifecycleDiagnostic").GetString().ShouldBe("live-execution");
    }

    private static System.Text.Json.JsonElement SerializeRoot(object? value)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        return System.Text.Json.JsonDocument.Parse(json).RootElement;
    }

    private static IAgentSupervisor BuildSupervisorWithHandle(
        string agentId, string sessionId, ContextDiagnostics diagnostics)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From(agentId));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From(sessionId));
        handle.As<IAgentHandleInspector>()
            .Setup(h => h.GetContextDiagnostics())
            .Returns(diagnostics);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetHandle(AgentId.From(agentId), SessionId.From(sessionId)))
            .Returns(handle.Object);
        return supervisor.Object;
    }
}

using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;

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
}

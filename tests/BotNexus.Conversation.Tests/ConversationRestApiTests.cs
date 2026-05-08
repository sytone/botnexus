using System.Net;
using System.Text.Json;
using Xunit.Abstractions;

namespace BotNexus.Conversation.Tests;

/// <summary>
/// Functional REST API tests for the Conversation endpoints.
/// Each test creates isolated data using unique agentIds to prevent state bleed.
/// </summary>
[Collection("LiveGateway")]
public class ConversationRestApiTests(LiveGatewayFixture fixture, ITestOutputHelper output)
{
    // ── GET /api/conversations ────────────────────────────────────────────────

    [SkippableFact]
    public async Task GetConversations_WithUnusedAgentId_ReturnsEmptyArray()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var uniqueAgentId = $"test-agent-{Guid.NewGuid():N}";
        var response = await fixture.Conversations.GetConversationsAsync(uniqueAgentId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        var arr = JsonDocument.Parse(json).RootElement;
        arr.ValueKind.ShouldBe(JsonValueKind.Array);
        arr.GetArrayLength().ShouldBe(0, "fresh agentId should have no conversations");
    }

    // ── POST /api/conversations ───────────────────────────────────────────────

    [SkippableFact]
    public async Task CreateConversation_Returns201WithCorrectData()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var agentId = $"test-agent-{Guid.NewGuid():N}";
        var response = await fixture.Conversations.CreateConversationAsync(agentId, "Test Project Alpha");
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var json = await response.Content.ReadAsStringAsync();
        output.WriteLine($"CreateConversation response: {json}");
        var doc = JsonDocument.Parse(json).RootElement;

        doc.TryGetProperty("conversationId", out var convId).ShouldBeTrue();
        convId.GetString()![..2].ShouldBe("c_");

        doc.GetProperty("agentId").GetString().ShouldBe(agentId);
        doc.GetProperty("title").GetString().ShouldBe("Test Project Alpha");
        doc.GetProperty("isDefault").GetBoolean().ShouldBe(false);
        doc.GetProperty("status").GetString().ShouldBe("Active");

        doc.TryGetProperty("bindings", out var bindings).ShouldBeTrue();
        bindings.ValueKind.ShouldBe(JsonValueKind.Array);
        bindings.GetArrayLength().ShouldBe(0);
    }

    [SkippableFact]
    public async Task CreateConversation_MissingAgentId_Returns400()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var response = await fixture.Conversations.CreateConversationRawAsync(new { title = "No Agent" });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ── GET /api/conversations — created conversation appears in list ──────────

    [SkippableFact]
    public async Task GetConversations_CreatedConversationAppearsInList()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var agentId = $"test-agent-{Guid.NewGuid():N}";
        var createResponse = await fixture.Conversations.CreateConversationAsync(agentId, "Listed Conversation");
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var createdId = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("conversationId").GetString()!;

        var listResponse = await fixture.Conversations.GetConversationsAsync(agentId);
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var items = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray().ToList();
        items.Count.ShouldBeGreaterThanOrEqualTo(1);

        var match = items.FirstOrDefault(i =>
            i.TryGetProperty("conversationId", out var id) && id.GetString() == createdId);
        match.ValueKind.ShouldNotBe(JsonValueKind.Undefined, "created conversation must appear in list");

        // Verify all required fields are present on the summary
        match.TryGetProperty("conversationId", out _).ShouldBeTrue();
        match.TryGetProperty("agentId", out _).ShouldBeTrue();
        match.TryGetProperty("title", out _).ShouldBeTrue();
        match.TryGetProperty("isDefault", out _).ShouldBeTrue();
        match.TryGetProperty("status", out _).ShouldBeTrue();
        match.TryGetProperty("bindingCount", out _).ShouldBeTrue();
        match.TryGetProperty("createdAt", out _).ShouldBeTrue();
        match.TryGetProperty("updatedAt", out _).ShouldBeTrue();
    }

    // ── GET /api/conversations/{id} ───────────────────────────────────────────

    [SkippableFact]
    public async Task GetConversation_ReturnsFullConversation()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var agentId = $"test-agent-{Guid.NewGuid():N}";
        var createResponse = await fixture.Conversations.CreateConversationAsync(agentId, "Fetch By Id Test");
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var createdId = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("conversationId").GetString()!;

        var response = await fixture.Conversations.GetConversationAsync(createdId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("conversationId").GetString().ShouldBe(createdId);
        doc.GetProperty("title").GetString().ShouldBe("Fetch By Id Test");
        doc.TryGetProperty("bindings", out var bindings).ShouldBeTrue();
        bindings.ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [SkippableFact]
    public async Task GetConversation_UnknownId_Returns404()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var response = await fixture.Conversations.GetConversationAsync("c_doesnotexist");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── PATCH /api/conversations/{id} ─────────────────────────────────────────

    [SkippableFact]
    public async Task PatchConversation_TitleUpdatePersists()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var agentId = $"test-agent-{Guid.NewGuid():N}";
        var createResponse = await fixture.Conversations.CreateConversationAsync(agentId, "Original");
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var createdId = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("conversationId").GetString()!;

        var patchResponse = await fixture.Conversations.PatchConversationAsync(createdId, "Updated Title");
        patchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var patchDoc = JsonDocument.Parse(await patchResponse.Content.ReadAsStringAsync()).RootElement;
        patchDoc.GetProperty("title").GetString().ShouldBe("Updated Title");

        // Fetch again to verify persistence
        var fetchResponse = await fixture.Conversations.GetConversationAsync(createdId);
        fetchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var fetchDoc = JsonDocument.Parse(await fetchResponse.Content.ReadAsStringAsync()).RootElement;
        fetchDoc.GetProperty("title").GetString().ShouldBe("Updated Title");
    }

    // ── GET /api/conversations/{id}/history ───────────────────────────────────

    [SkippableFact]
    public async Task GetConversationHistory_EmptyOnNewConversation()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var agentId = $"test-agent-{Guid.NewGuid():N}";
        var createResponse = await fixture.Conversations.CreateConversationAsync(agentId, "History Test");
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var createdId = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("conversationId").GetString()!;

        var histResponse = await fixture.Conversations.GetConversationHistoryAsync(createdId);
        histResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var doc = JsonDocument.Parse(await histResponse.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("conversationId").GetString().ShouldBe(createdId);
        doc.TryGetProperty("entries", out var entries).ShouldBeTrue();
        entries.ValueKind.ShouldBe(JsonValueKind.Array);
        doc.GetProperty("totalCount").GetInt32().ShouldBe(0);
        doc.GetProperty("offset").GetInt32().ShouldBe(0);
    }

    [SkippableFact]
    public async Task GetConversationHistory_UnknownId_Returns404()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var response = await fixture.Conversations.GetConversationHistoryAsync("c_doesnotexist");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── GET /api/conversations?agentId=nonexistent ────────────────────────────

    [SkippableFact]
    public async Task GetConversations_NonexistentAgentId_ReturnsEmptyArrayNot404()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var response = await fixture.Conversations.GetConversationsAsync("agent-that-definitely-does-not-exist-xyz");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var arr = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        arr.ValueKind.ShouldBe(JsonValueKind.Array);
        arr.GetArrayLength().ShouldBe(0);
    }
}

using System.Net;
using System.Text.Json;
using Xunit.Abstractions;

namespace BotNexus.Conversation.Tests;

/// <summary>
/// Candidate seam regressions covering conversation and session-history projections.
/// Each test is isolated with a unique agentId.
/// </summary>
[Collection("CandidateGateway")]
public class ProbeRound2Tests(CandidateGatewayFixture fixture, ITestOutputHelper output)
{
    // ── Conversations: two conversations for same agent are independent ────────

    [Fact]
    public async Task TwoConversations_SameAgent_AreIndependent()
    {
        var agentId = fixture.NewAgent();

        var r1 = await fixture.Conversations.CreateConversationAsync(agentId, "Conversation A");
        r1.StatusCode.ShouldBe(HttpStatusCode.Created);
        var idA = JsonDocument.Parse(await r1.Content.ReadAsStringAsync()).RootElement.GetProperty("conversationId").GetString()!;

        var r2 = await fixture.Conversations.CreateConversationAsync(agentId, "Conversation B");
        r2.StatusCode.ShouldBe(HttpStatusCode.Created);
        var idB = JsonDocument.Parse(await r2.Content.ReadAsStringAsync()).RootElement.GetProperty("conversationId").GetString()!;

        idA.ShouldNotBe(idB, "two conversations for same agent must have distinct IDs");

        // Both appear in list
        var listResp = await fixture.Conversations.GetConversationsAsync(agentId);
        listResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray().ToList();

        var ids = items.Select(i => i.GetProperty("conversationId").GetString()).ToHashSet();
        ids.ShouldContain(idA);
        ids.ShouldContain(idB);

        // Titles are distinct
        var titleA = items.First(i => i.GetProperty("conversationId").GetString() == idA).GetProperty("title").GetString();
        var titleB = items.First(i => i.GetProperty("conversationId").GetString() == idB).GetProperty("title").GetString();
        titleA.ShouldBe("Conversation A");
        titleB.ShouldBe("Conversation B");
    }

    // ── Conversations: history has entries with correct roles after messages ──

    [Fact]
    public async Task ConversationHistory_AfterMessagesViaDefaultConversation_HasEntries()
    {
        var convId = fixture.SeedConversationId;
        var histResp = await fixture.Conversations.GetConversationHistoryAsync(convId);
        histResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var doc = JsonDocument.Parse(await histResp.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("conversationId").GetString().ShouldBe(convId);

        var entries = doc.GetProperty("entries").EnumerateArray().ToList();
        var totalCount = doc.GetProperty("totalCount").GetInt32();
        output.WriteLine($"History: totalCount={totalCount}, entries returned={entries.Count}");

        totalCount.ShouldBe(3, "seeded conversation must include all three history rows");
        entries.Select(e => e.GetProperty("role").GetString()).ShouldBe(new[] { "user", "tool", "assistant" });
        entries.Select(e => e.GetProperty("content").GetString()).ShouldBe(new[] { "seed question", "seed tool failed", "seed answer" });

        // Every entry should have role field
        foreach (var entry in entries)
        {
            entry.TryGetProperty("role", out var role).ShouldBeTrue("history entry must have role field");
            role.GetString().ShouldNotBeNullOrEmpty("role must not be empty");
        }
    }

    // ── Sessions: every session in GET /api/sessions has conversationId field present ──

    [Fact]
    public async Task GetSessions_AllSessionsHaveConversationIdField()
    {
        var resp = await fixture.Http.GetAsync("/api/sessions");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var sessions = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessions").EnumerateArray().ToList();
        sessions.ShouldNotBeEmpty();

        output.WriteLine($"Sessions count: {sessions.Count}");

        // Every session object must have the conversationId field (value may be null for legacy sessions
        // but the field itself must exist after PR #85)
        foreach (var session in sessions)
        {
            var hasField = session.TryGetProperty("conversationId", out var conversationId);
            conversationId.GetString().ShouldNotBeNullOrEmpty();
            output.WriteLine($"  session={session.GetProperty("sessionId").GetString()} conversationId field present={hasField} agentId={session.GetProperty("agentId").GetString()}");
            hasField.ShouldBeTrue($"session {session.GetProperty("sessionId").GetString()} must have conversationId field in GET /api/sessions response");
        }
    }

    // ── Sessions: GET /api/sessions/{id}/history has toolArgs and toolIsError fields ──

    [Fact]
    public async Task SessionHistory_ToolCallEntries_HaveToolArgsAndToolIsError()
    {
        var sessionId = CandidateGatewayFixture.SeedSessionId;
        var histResp = await fixture.Http.GetAsync($"/api/sessions/{sessionId}/history?limit=50");
        histResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var histDoc = JsonDocument.Parse(await histResp.Content.ReadAsStringAsync()).RootElement;

        // Check via /api/sessions/{id} (full session with history)
        var fullResp = await fixture.Http.GetAsync($"/api/sessions/{sessionId}");
        fullResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var fullDoc = JsonDocument.Parse(await fullResp.Content.ReadAsStringAsync()).RootElement;
        JsonElement historyArray = default;
        bool found = fullDoc.TryGetProperty("history", out historyArray) ||
                     (fullDoc.TryGetProperty("session", out var sess) && sess.TryGetProperty("history", out historyArray));

        found.ShouldBeTrue("history must be present in full session response");
        var entries = historyArray.EnumerateArray().ToList();
        entries.Count.ShouldBe(3);
        var tool = entries.Single(e => e.GetProperty("role").GetString() == "tool");
        tool.GetProperty("toolName").GetString().ShouldBe("seed_tool");
        tool.GetProperty("toolArgs").GetString().ShouldBe("{\"value\":42}");
        tool.GetProperty("toolIsError").GetBoolean().ShouldBeTrue();
        histDoc.GetProperty("totalCount").GetInt32().ShouldBe(3);
        histDoc.GetProperty("entries").GetArrayLength().ShouldBe(3);

        // Every entry must have toolArgs and toolIsError fields (from PR #68)
        foreach (var entry in entries.Take(20))
        {
            entry.TryGetProperty("toolArgs", out _).ShouldBeTrue(
                $"history entry with role={entry.GetProperty("role").GetString()} must have toolArgs field");
            entry.TryGetProperty("toolIsError", out _).ShouldBeTrue(
                $"history entry with role={entry.GetProperty("role").GetString()} must have toolIsError field");
        }
    }

    // ── Conversations: GET /api/conversations/{id} has activeSessionId field ──

    [Fact]
    public async Task GetConversation_ActiveSessionIdFieldPresent()
    {
        var agentId = fixture.NewAgent();
        var createResp = await fixture.Conversations.CreateConversationAsync(agentId, "ActiveSession Test");
        createResp.StatusCode.ShouldBe(HttpStatusCode.Created);
        var convId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("conversationId").GetString()!;

        var getResp = await fixture.Conversations.GetConversationAsync(convId);
        getResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var doc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync()).RootElement;
        // activeSessionId must be present as a field (null is fine for new conversation)
        doc.TryGetProperty("activeSessionId", out var activeSessionId).ShouldBeTrue(
            "GET /api/conversations/{id} must return activeSessionId field");
        activeSessionId.ValueKind.ShouldBe(JsonValueKind.Null);
    }
}

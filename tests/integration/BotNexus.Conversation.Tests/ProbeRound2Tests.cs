using System.Net;
using System.Text.Json;
using Xunit.Abstractions;

namespace BotNexus.Conversation.Tests;

/// <summary>
/// Probe Round 2 — live integration tests covering gaps identified by full workflow probing.
/// Each test is isolated with a unique agentId.
/// </summary>
[Collection("LiveGateway")]
public class ProbeRound2Tests(LiveGatewayFixture fixture, ITestOutputHelper output)
{
    // ── Conversations: two conversations for same agent are independent ────────

    [SkippableFact]
    public async Task TwoConversations_SameAgent_AreIndependent()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var agentId = $"probe2-agent-{Guid.NewGuid():N}";

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

    [SkippableFact]
    public async Task ConversationHistory_AfterMessagesViaDefaultConversation_HasEntries()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        // Use the default assistant agent which already has a conversation and history
        var listResp = await fixture.Http.GetAsync("/api/conversations?agentId=assistant");
        Skip.If(!listResp.IsSuccessStatusCode, "assistant agent not available");

        var convs = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToList();
        Skip.If(convs.Count == 0, "assistant has no conversations");

        var convId = convs.First().GetProperty("conversationId").GetString()!;
        output.WriteLine($"Probing conversation history for {convId}");

        var histResp = await fixture.Conversations.GetConversationHistoryAsync(convId);
        histResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var doc = JsonDocument.Parse(await histResp.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("conversationId").GetString().ShouldBe(convId);

        var entries = doc.GetProperty("entries").EnumerateArray().ToList();
        var totalCount = doc.GetProperty("totalCount").GetInt32();
        output.WriteLine($"History: totalCount={totalCount}, entries returned={entries.Count}");

        totalCount.ShouldBeGreaterThan(0, "conversation with messages must have positive totalCount");

        // Every entry should have role field
        foreach (var entry in entries)
        {
            entry.TryGetProperty("role", out var role).ShouldBeTrue("history entry must have role field");
            role.GetString().ShouldNotBeNullOrEmpty("role must not be empty");
        }
    }

    // ── Sessions: every session in GET /api/sessions has conversationId field present ──

    [SkippableFact]
    public async Task GetSessions_AllSessionsHaveConversationIdField()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var resp = await fixture.Http.GetAsync("/api/sessions");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var sessions = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray().ToList();
        Skip.If(sessions.Count == 0, "no sessions present");

        output.WriteLine($"Sessions count: {sessions.Count}");

        // Every session object must have the conversationId field (value may be null for legacy sessions
        // but the field itself must exist after PR #85)
        foreach (var session in sessions)
        {
            var hasField = session.TryGetProperty("conversationId", out _);
            output.WriteLine($"  session={session.GetProperty("sessionId").GetString()} conversationId field present={hasField} agentId={session.GetProperty("agentId").GetString()}");
            hasField.ShouldBeTrue($"session {session.GetProperty("sessionId").GetString()} must have conversationId field in GET /api/sessions response");
        }
    }

    // ── Sessions: GET /api/sessions/{id}/history has toolArgs and toolIsError fields ──

    [SkippableFact]
    public async Task SessionHistory_ToolCallEntries_HaveToolArgsAndToolIsError()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        // Find a session with tool calls in its history
        var sessionsResp = await fixture.Http.GetAsync("/api/sessions");
        sessionsResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sessions = JsonDocument.Parse(await sessionsResp.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray().ToList();
        Skip.If(sessions.Count == 0, "no sessions present");

        // Pick the session with highest message count (most likely to have tool calls)
        var targetSession = sessions
            .OrderByDescending(s => s.TryGetProperty("messageCount", out var mc) ? mc.GetInt32() : 0)
            .First();
        var sessionId = targetSession.GetProperty("sessionId").GetString()!;
        output.WriteLine($"Probing session history for {sessionId}");

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

        Skip.If(!found, "history not available in session response");
        var entries = historyArray.EnumerateArray().ToList();
        Skip.If(entries.Count == 0, "session has no history entries");

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

    [SkippableFact]
    public async Task GetConversation_ActiveSessionIdFieldPresent()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var agentId = $"probe2-active-{Guid.NewGuid():N}";
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
        output.WriteLine($"activeSessionId: {activeSessionId}");
    }
}

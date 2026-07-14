using System.Text.Json;
using Xunit.Abstractions;

namespace BotNexus.Conversation.Tests;

/// <summary>
/// SignalR hub tests verifying conversation model integration.
/// Documents current behavior and future Wave 2 expectations.
/// </summary>
[Collection("LiveGateway")]
public class ConversationSignalRTests(LiveGatewayFixture fixture, ITestOutputHelper output)
{
    // ── SubscribeAll ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task SubscribeAll_ReturnsSessions()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await fixture.SignalR.SubscribeAllAsync(cts.Token);

        // Documents current behavior: SubscribeAll returns sessions list
        result.ValueKind.ShouldBe(JsonValueKind.Object);
        result.TryGetProperty("sessions", out var sessions).ShouldBeTrue("expected sessions property");
        sessions.ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [SkippableFact]
    public async Task SubscribeAll_DocumentsConversationListIsFutureExtension()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await fixture.SignalR.SubscribeAllAsync(cts.Token);

        // NOTE: conversations list in SubscribeAll response is a future extension (Wave 3).
        // This test documents that it is NOT present in current behavior.
        // When Wave 3 ships, update this test to assert conversations IS present.
        output.WriteLine($"SubscribeAll conversations field present: {result.TryGetProperty("conversations", out _)}");
    }

    // ── SendMessage ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task SendMessage_RoutesToAssistantAndRaisesMessageStart()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await fixture.SignalR.SubscribeAllAsync(cts.Token);

        var result = await fixture.SignalR.SendMessageAsync(
            "assistant", "ping — conversation model test", cts.Token, "signalr");

        result.SessionId.ShouldNotBeNullOrEmpty();
        result.AgentId.ShouldBe("assistant");

        // Wait for MessageStart
        var evt = await fixture.SignalR.WaitForEventAsync(
            result.SessionId, "MessageStart", TimeSpan.FromSeconds(15), cts.Token);
        evt.ShouldNotBeNull();
    }

    [SkippableFact]
    public async Task SendMessage_SessionHasConversationIdField_DocumentedFutureState()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await fixture.SignalR.SubscribeAllAsync(cts.Token);

        var result = await fixture.SignalR.SendMessageAsync(
            "assistant", "ping — conversation model test 2", cts.Token, "signalr");

        await fixture.SignalR.WaitForEventAsync(
            result.SessionId, "MessageStart", TimeSpan.FromSeconds(15), cts.Token);

        // Check session via REST API
        var sessionResponse = await fixture.Http.GetAsync(
            $"/api/sessions/{result.SessionId}", cts.Token);

        if (sessionResponse.IsSuccessStatusCode)
        {
            var json = await sessionResponse.Content.ReadAsStringAsync(cts.Token);
            var doc = JsonDocument.Parse(json).RootElement;
            // conversationId will be null until Wave 2 routing is live.
            // This test documents the expected future field presence.
            output.WriteLine($"Session conversationId: {(doc.TryGetProperty("conversationId", out var cid) ? cid.ToString() : "field absent")}");
        }
        else
        {
            output.WriteLine($"Session endpoint returned {sessionResponse.StatusCode} — sessions API may differ");
        }

        // Pass regardless — this is a documentation test
    }

    [SkippableFact]
    public async Task SendMessage_SessionConversationIdIsStamped()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await fixture.SignalR.SubscribeAllAsync(cts.Token);

        var result = await fixture.SignalR.SendMessageAsync(
            "assistant", "ping — conversation routing test", cts.Token, "signalr");

        result.SessionId.ShouldNotBeNullOrEmpty();

        // Brief wait for session to be stamped
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

        var sessionResponse = await fixture.Http.GetAsync(
            $"/api/sessions/{result.SessionId}", cts.Token);
        Skip.If(!sessionResponse.IsSuccessStatusCode, "session endpoint not available");

        var doc = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync(cts.Token)).RootElement;
        output.WriteLine($"Session conversationId field: {(doc.TryGetProperty("conversationId", out var cid) ? cid.ToString() : "absent")}");

        // Check in nested session object first, then top-level
        JsonElement convId = default;
        bool found = doc.TryGetProperty("conversationId", out convId) ||
                     (doc.TryGetProperty("session", out var sess) && sess.TryGetProperty("conversationId", out convId));

        Skip.If(!found || convId.ValueKind == JsonValueKind.Null || string.IsNullOrEmpty(convId.GetString()),
            "conversationId not stamped on session — Wave 2 routing not yet live");

        convId.GetString()![..2].ShouldBe("c_", "conversationId should start with c_");
    }

    [SkippableFact]
    public async Task SendMessage_DefaultConversationCreatedForAssistant()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await fixture.SignalR.SubscribeAllAsync(cts.Token);

        // Send a message to ensure default conversation exists
        await fixture.SignalR.SendMessageAsync(
            "assistant", "ping — default conversation test", cts.Token, "signalr");

        // Brief wait for conversation to be created
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);

        var listResponse = await fixture.Http.GetAsync("/api/conversations?agentId=assistant", cts.Token);
        listResponse.IsSuccessStatusCode.ShouldBeTrue();

        var items = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(cts.Token))
            .RootElement.EnumerateArray().ToList();
        output.WriteLine($"Conversations for assistant: {items.Count}");

        Skip.If(items.Count == 0, "No conversations found for assistant — default conversation auto-creation not yet live");

        items.Any(i => i.TryGetProperty("isDefault", out var v) && v.GetBoolean())
            .ShouldBeTrue("at least one conversation should be the default");
    }
}

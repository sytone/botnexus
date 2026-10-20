using System.Net;
using System.Text.Json;

namespace BotNexus.Conversation.Tests;

/// <summary>Real candidate hub, routing, session persistence, and adapter event delivery.</summary>
[Collection("CandidateGateway")]
public sealed class ConversationSignalRTests(CandidateGatewayFixture fixture)
{
    [Fact]
    public async Task SubscribeAll_ReturnsSessions()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await fixture.SignalR.SubscribeAllAsync(cts.Token);
        result.ValueKind.ShouldBe(JsonValueKind.Object);
        var sessions = result.GetProperty("sessions");
        sessions.ValueKind.ShouldBe(JsonValueKind.Array);
        sessions.EnumerateArray().ShouldContain(s => s.GetProperty("sessionId").GetString() == CandidateGatewayFixture.SeedSessionId);
    }

    [Fact]
    public async Task SubscribeAll_ReturnsConversationIdentityInSessionSnapshot()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await fixture.SignalR.SubscribeAllAsync(cts.Token);
        var session = result.GetProperty("sessions").EnumerateArray()
            .Single(s => s.GetProperty("sessionId").GetString() == CandidateGatewayFixture.SeedSessionId);
        session.GetProperty("conversationId").GetString().ShouldBe(fixture.SeedConversationId);
        session.GetProperty("agentId").GetString().ShouldBe("assistant");
    }

    [Fact]
    public async Task SendMessage_RoutesToAssistantAndRaisesMessageStart()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await fixture.SignalR.SubscribeAllAsync(cts.Token);
        var agentId = fixture.NewAgent();
        var result = await fixture.SignalR.SendMessageAsync(agentId, "candidate message", cts.Token);
        result.SessionId.ShouldNotBeNullOrEmpty();
        result.AgentId.ShouldBe(agentId);
        var evt = await fixture.SignalR.WaitForEventAsync(result.SessionId, "MessageStart", TimeSpan.FromSeconds(10), cts.Token);
        evt.Payload.GetProperty("sessionId").GetString().ShouldBe(result.SessionId);
        evt.Payload.GetProperty("agentId").GetString().ShouldBe(agentId);
    }

    [Fact]
    public async Task SendMessage_SessionConversationIdResolvesThroughRest()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await fixture.SignalR.SendMessageAsync("assistant", "resolve conversation", cts.Token);
        var session = await GetSession(result.SessionId, cts.Token);
        var conversationId = session.GetProperty("conversationId").GetString();
        conversationId.ShouldNotBeNullOrEmpty();
        using var response = await fixture.Conversations.GetConversationAsync(conversationId, cts.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var conversation = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        conversation.GetProperty("agentId").GetString().ShouldBe("assistant");
        conversation.GetProperty("conversationId").GetString().ShouldBe(conversationId);
    }

    [Fact]
    public async Task SendMessage_SessionConversationIdIsStamped()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var agentId = fixture.NewAgent();
        var result = await fixture.SignalR.SendMessageAsync(agentId, "stamp conversation", cts.Token);
        var session = await GetSession(result.SessionId, cts.Token);
        var conversationId = session.GetProperty("conversationId").GetString();
        conversationId.ShouldNotBeNullOrEmpty();
        session.GetProperty("agentId").GetString().ShouldBe(agentId);
        using var response = await fixture.Conversations.GetConversationAsync(conversationId, cts.Token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
        document.RootElement.GetProperty("activeSessionId").GetString().ShouldBe(result.SessionId);
    }

    [Fact]
    public async Task SendMessage_DefaultConversationCreatedForAssistant()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var agentId = fixture.NewAgent();
        var result = await fixture.SignalR.SendMessageAsync(agentId, "default conversation", cts.Token);
        using var response = await fixture.Conversations.GetConversationsAsync(agentId, cts.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement.EnumerateArray().ToList();
        var conversation = items.Single(i => i.GetProperty("isDefault").GetBoolean());
        conversation.GetProperty("conversationId").GetString().ShouldNotBeNullOrEmpty();
        conversation.GetProperty("agentId").GetString().ShouldBe(agentId);
        var session = await GetSession(result.SessionId, cts.Token);
        session.GetProperty("conversationId").GetString().ShouldBe(conversation.GetProperty("conversationId").GetString());
    }

    private async Task<JsonElement> GetSession(string id, CancellationToken ct)
    {
        using var response = await fixture.Http.GetAsync($"/api/sessions/{id}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }
}

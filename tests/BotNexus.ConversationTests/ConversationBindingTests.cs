using System.Net;
using System.Text.Json;
using Xunit.Abstractions;

namespace BotNexus.ConversationTests;

/// <summary>
/// Functional tests for conversation channel binding endpoints.
/// Each test uses unique agentIds to prevent state bleed.
/// </summary>
[Collection("LiveGateway")]
public class ConversationBindingTests(LiveGatewayFixture fixture, ITestOutputHelper output)
{
    // ── POST /api/conversations/{id}/bindings ─────────────────────────────────

    [SkippableFact]
    public async Task AddBinding_Returns201WithCorrectData()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var convId = await CreateConversationAsync();
        var response = await fixture.Conversations.AddBindingAsync(
            convId, "telegram", "1234567890", "Single");
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var json = await response.Content.ReadAsStringAsync();
        output.WriteLine($"AddBinding response: {json}");
        var doc = JsonDocument.Parse(json).RootElement;

        doc.TryGetProperty("bindingId", out var bindingId).ShouldBeTrue();
        var bidStr = bindingId.GetString()!;
        bidStr.ShouldNotBeNullOrEmpty();
        bidStr.Length.ShouldBe(32, "bindingId should be 32-char hex (no hyphens)");
        bidStr.ShouldNotContain("-");

        doc.GetProperty("channelType").GetString().ShouldBe("telegram");
        doc.GetProperty("channelAddress").GetString().ShouldBe("1234567890");

        doc.TryGetProperty("threadId", out var threadId).ShouldBeTrue();
        threadId.ValueKind.ShouldBe(JsonValueKind.Null);

        doc.GetProperty("mode").GetString().ShouldBe("Interactive");
        doc.GetProperty("threadingMode").GetString().ShouldBe("Single");
    }

    [SkippableFact]
    public async Task AddBinding_UnknownConversationId_Returns404()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var response = await fixture.Conversations.AddBindingAsync(
            "c_doesnotexist", "telegram", "1234567890", "Single");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task AddBinding_NativeThreadWithThreadId_PreservesThreadId()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var convId = await CreateConversationAsync();
        var response = await fixture.Conversations.AddBindingFullAsync(
            convId, "teams", "channel-abc", "thread-xyz", "NativeThread");
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("threadId").GetString().ShouldBe("thread-xyz");
        doc.GetProperty("threadingMode").GetString().ShouldBe("NativeThread");
        var bindingId = doc.GetProperty("bindingId").GetString()!;

        // Fetch conversation and verify binding is preserved
        var convResponse = await fixture.Conversations.GetConversationAsync(convId);
        convResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var convDoc = JsonDocument.Parse(await convResponse.Content.ReadAsStringAsync()).RootElement;
        var binding = convDoc.GetProperty("bindings").EnumerateArray()
            .FirstOrDefault(b => b.TryGetProperty("bindingId", out var bid) && bid.GetString() == bindingId);
        binding.ValueKind.ShouldNotBe(JsonValueKind.Undefined);
        binding.GetProperty("threadId").GetString().ShouldBe("thread-xyz");
        binding.GetProperty("threadingMode").GetString().ShouldBe("NativeThread");
    }

    // ── GET conversation after add ────────────────────────────────────────────

    [SkippableFact]
    public async Task GetConversation_AfterAddBinding_BindingAppearsInArray()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var convId = await CreateConversationAsync();
        var addResponse = await fixture.Conversations.AddBindingAsync(
            convId, "telegram", "1234567890", "Single");
        addResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var bindingId = JsonDocument.Parse(await addResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("bindingId").GetString()!;

        var convResponse = await fixture.Conversations.GetConversationAsync(convId);
        convResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await convResponse.Content.ReadAsStringAsync()).RootElement;

        doc.TryGetProperty("bindings", out var bindings).ShouldBeTrue();
        bindings.ValueKind.ShouldBe(JsonValueKind.Array);
        bindings.GetArrayLength().ShouldBe(1);

        var binding = bindings.EnumerateArray().First();
        binding.GetProperty("bindingId").GetString().ShouldBe(bindingId);
        binding.GetProperty("channelType").GetString().ShouldBe("telegram");
        binding.GetProperty("channelAddress").GetString().ShouldBe("1234567890");
    }

    [SkippableFact]
    public async Task GetConversationsList_AfterAddBinding_BindingCountIsOne()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var agentId = $"test-agent-{Guid.NewGuid():N}";
        var createResponse = await fixture.Conversations.CreateConversationAsync(agentId, "BindingCount Test");
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var convId = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("conversationId").GetString()!;

        var addResponse = await fixture.Conversations.AddBindingAsync(convId, "telegram", "1234567890", "Single");
        addResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var listResponse = await fixture.Conversations.GetConversationsAsync(agentId);
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray().ToList();
        var match = items.FirstOrDefault(i =>
            i.TryGetProperty("conversationId", out var id) && id.GetString() == convId);
        match.ValueKind.ShouldNotBe(JsonValueKind.Undefined);
        match.GetProperty("bindingCount").GetInt32().ShouldBe(1);
    }

    // ── DELETE /api/conversations/{id}/bindings/{bindingId} ───────────────────

    [SkippableFact]
    public async Task DeleteBinding_Returns204AndBindingIsGone()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var convId = await CreateConversationAsync();
        var addResponse = await fixture.Conversations.AddBindingAsync(
            convId, "telegram", "1234567890", "Single");
        addResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var bindingId = JsonDocument.Parse(await addResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("bindingId").GetString()!;

        var deleteResponse = await fixture.Conversations.DeleteBindingAsync(convId, bindingId);
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Verify binding is gone
        var convResponse = await fixture.Conversations.GetConversationAsync(convId);
        convResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await convResponse.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("bindings").GetArrayLength().ShouldBe(0);
    }

    [SkippableFact]
    public async Task DeleteBinding_UnknownBindingId_Returns404()
    {
        Skip.If(!fixture.IsAvailable, "Dev gateway not running at localhost:5006");

        var convId = await CreateConversationAsync();
        var response = await fixture.Conversations.DeleteBindingAsync(convId, "doesnotexist");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> CreateConversationAsync()
    {
        var agentId = $"test-agent-{Guid.NewGuid():N}";
        var response = await fixture.Conversations.CreateConversationAsync(agentId, "Binding Test");
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("conversationId").GetString()!;
    }
}

using System.Net;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Gateway.Tests.Api;

[Trait("Category", "Integration")]
[Collection("IntegrationTests")]
public sealed class ChannelHistoryTests
{
    [Fact]
    public async Task NoCursor_ReturnsNewestMessages()
    {
        var store = CreateStoreWithSingleSession("agent-a", "web", "s-new", 60, DateTimeOffset.UtcNow.AddHours(-1));
        await using var factory = CreateTestFactory(store);
        using var client = factory.CreateClient();

        using var payload = await GetHistoryJsonAsync(client, "web", "agent-a");

        payload.RootElement.GetProperty("messages").GetArrayLength().ShouldBe(50);
        payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString().ShouldBe("m-10");
        payload.RootElement.GetProperty("messages")[49].GetProperty("content").GetString().ShouldBe("m-59");
        payload.RootElement.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task WithCursor_ReturnsPreviousBatch()
    {
        var store = CreateStoreWithSingleSession("agent-a", "web", "s-new", 70, DateTimeOffset.UtcNow.AddHours(-1));
        await using var factory = CreateTestFactory(store);
        using var client = factory.CreateClient();

        using var first = await GetHistoryJsonAsync(client, "web", "agent-a");
        var cursor = first.RootElement.GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNullOrWhiteSpace();

        using var second = await GetHistoryJsonAsync(client, "web", "agent-a", cursor!, limit: 50);

        second.RootElement.GetProperty("messages").GetArrayLength().ShouldBe(20);
        second.RootElement.GetProperty("messages")[0].GetProperty("content").GetString().ShouldBe("m-0");
        second.RootElement.GetProperty("messages")[19].GetProperty("content").GetString().ShouldBe("m-19");
    }

    [Fact]
    public async Task CrossSession_SpansSessions()
    {
        var store = new InMemorySessionStore();
        await SeedSessionAsync(store, "s-old", "agent-a", "web", 40, DateTimeOffset.UtcNow.AddHours(-2));
        await SeedSessionAsync(store, "s-new", "agent-a", "web", 30, DateTimeOffset.UtcNow.AddHours(-1));
        await using var factory = CreateTestFactory(store);
        using var client = factory.CreateClient();

        using var payload = await GetHistoryJsonAsync(client, "web", "agent-a", limit: 50);
        var messages = payload.RootElement.GetProperty("messages");
        var sessionIds = messages.EnumerateArray().Select(m => m.GetProperty("sessionId").GetString()).Distinct().ToArray();

        messages.GetArrayLength().ShouldBe(50);
        sessionIds.ShouldContain("s-old");
        sessionIds.ShouldContain("s-new");
    }

    [Fact]
    public async Task SessionBoundaries_IncludedInResponse()
    {
        var store = new InMemorySessionStore();
        await SeedSessionAsync(store, "s-old", "agent-a", "web", 40, DateTimeOffset.UtcNow.AddHours(-2));
        await SeedSessionAsync(store, "s-new", "agent-a", "web", 30, DateTimeOffset.UtcNow.AddHours(-1));
        await using var factory = CreateTestFactory(store);
        using var client = factory.CreateClient();

        using var payload = await GetHistoryJsonAsync(client, "web", "agent-a", limit: 50);
        var boundaries = payload.RootElement.GetProperty("sessionBoundaries");

        boundaries.GetArrayLength().ShouldBeGreaterThan(0);
        var firstBoundary = boundaries[0];
        firstBoundary.GetProperty("insertBeforeIndex").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
        firstBoundary.GetProperty("sessionId").GetString().ShouldNotBeNullOrWhiteSpace();
        firstBoundary.GetProperty("startedAt").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task EmptySession_Skipped()
    {
        var store = new InMemorySessionStore();
        await SeedSessionAsync(store, "s-old", "agent-a", "web", 60, DateTimeOffset.UtcNow.AddHours(-3));
        await SeedSessionAsync(store, "s-empty", "agent-a", "web", 0, DateTimeOffset.UtcNow.AddHours(-2));
        await SeedSessionAsync(store, "s-new", "agent-a", "web", 5, DateTimeOffset.UtcNow.AddHours(-1));
        await using var factory = CreateTestFactory(store);
        using var client = factory.CreateClient();

        using var payload = await GetHistoryJsonAsync(client, "web", "agent-a", limit: 50);
        var sessionIds = payload.RootElement.GetProperty("messages")
            .EnumerateArray()
            .Select(m => m.GetProperty("sessionId").GetString())
            .Distinct()
            .ToArray();

        sessionIds.ShouldNotContain("s-empty");
    }

    [Fact]
    public async Task HasMoreFalse_AtEndOfHistory()
    {
        var store = CreateStoreWithSingleSession("agent-a", "web", "s-only", 7, DateTimeOffset.UtcNow.AddHours(-1));
        await using var factory = CreateTestFactory(store);
        using var client = factory.CreateClient();

        using var payload = await GetHistoryJsonAsync(client, "web", "agent-a", limit: 50);

        payload.RootElement.GetProperty("hasMore").GetBoolean().ShouldBeFalse();
        payload.RootElement.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task LimitRespected()
    {
        var store = CreateStoreWithSingleSession("agent-a", "web", "s-limit", 30, DateTimeOffset.UtcNow.AddHours(-1));
        await using var factory = CreateTestFactory(store);
        using var client = factory.CreateClient();

        using var payload = await GetHistoryJsonAsync(client, "web", "agent-a", limit: 10);

        payload.RootElement.GetProperty("messages").GetArrayLength().ShouldBeLessThanOrEqualTo(10);
    }

    [Fact]
    public async Task LimitClamped()
    {
        var store = CreateStoreWithSingleSession("agent-a", "web", "s-clamp", 250, DateTimeOffset.UtcNow.AddHours(-1));
        await using var factory = CreateTestFactory(store);
        using var client = factory.CreateClient();

        using var payload = await GetHistoryJsonAsync(client, "web", "agent-a", limit: 999);

        payload.RootElement.GetProperty("messages").GetArrayLength().ShouldBe(200);
    }

    [Fact]
    public async Task MessagesOrderedOldestFirst()
    {
        var store = CreateStoreWithSingleSession("agent-a", "web", "s-order", 20, DateTimeOffset.UtcNow.AddHours(-1));
        await using var factory = CreateTestFactory(store);
        using var client = factory.CreateClient();

        using var payload = await GetHistoryJsonAsync(client, "web", "agent-a", limit: 20);
        var timestamps = payload.RootElement.GetProperty("messages")
            .EnumerateArray()
            .Select(m => m.GetProperty("timestamp").GetDateTimeOffset())
            .ToArray();

        timestamps.ShouldBeInOrder(SortDirection.Ascending);
    }

    [Fact]
    public async Task InvalidCursor_Returns400()
    {
        var store = CreateStoreWithSingleSession("agent-a", "web", "s-invalid-cursor", 20, DateTimeOffset.UtcNow.AddHours(-1));
        await using var factory = CreateTestFactory(store);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/channels/web/agents/agent-a/history?cursor=not-a-valid-cursor");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static InMemorySessionStore CreateStoreWithSingleSession(
        string agentId,
        string channelType,
        string sessionId,
        int messageCount,
        DateTimeOffset createdAt)
    {
        var store = new InMemorySessionStore();
        SeedSessionAsync(store, sessionId, agentId, channelType, messageCount, createdAt).GetAwaiter().GetResult();
        return store;
    }

    private static async Task SeedSessionAsync(
        ISessionStore store,
        string sessionId,
        string agentId,
        string channelType,
        int messageCount,
        DateTimeOffset createdAt)
    {
        var session = new GatewaySession
        {
            SessionId = sessionId,
            AgentId = agentId,
            ChannelType = channelType,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

        for (var i = 0; i < messageCount; i++)
        {
            session.History.Add(new SessionEntry
            {
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"m-{i}",
                Timestamp = createdAt.AddMinutes(i)
            });
        }

        await store.SaveAsync(session, CancellationToken.None);
    }

    private static async Task<JsonDocument> GetHistoryJsonAsync(
        HttpClient client,
        string channelType,
        string agentId,
        string? cursor = null,
        int? limit = null)
    {
        var queryParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(cursor))
            queryParts.Add($"cursor={Uri.EscapeDataString(cursor)}");
        if (limit is not null)
            queryParts.Add($"limit={limit.Value}");

        var query = queryParts.Count == 0 ? string.Empty : $"?{string.Join("&", queryParts)}";
        var response = await client.GetAsync($"/api/channels/{channelType}/agents/{agentId}/history{query}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static WebApplicationFactory<Program> CreateTestFactory(ISessionStore store)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseUrls("http://127.0.0.1:0");
                builder.ConfigureServices(services =>
                {
                    var hostedServices = services
                        .Where(d => d.ServiceType == typeof(IHostedService))
                        .ToList();
                    foreach (var descriptor in hostedServices)
                        services.Remove(descriptor);
                });

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ISessionStore>();
                    services.AddSingleton(store);
                });
            });
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Unit tests for <see cref="GatewayRestClient"/>.
/// Verifies REST calls use correct URLs and return correct DTOs.
/// </summary>
public sealed class GatewayRestClientTests
{
    private const string BaseUrl = "http://gateway.test/api/";

    private static (GatewayRestClient client, MockHttpMessageHandler handler) CreateClient()
    {
        var handler = new MockHttpMessageHandler();
        var http = new HttpClient(handler);
        var client = new GatewayRestClient(http);
        client.Configure(BaseUrl);
        return (client, handler);
    }

    [Fact]
    public async Task GetAgentsAsync_calls_correct_url()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("/api/agents", JsonSerializer.Serialize(new[]
        {
            new { agentId = "a1", displayName = "Agent One" }
        }));

        var agents = await client.GetAgentsAsync();

        handler.LastRequestUrl.ShouldContain("/api/agents");
        agents.Count.ShouldBe(1);
        agents[0].AgentId.ShouldBe("a1");
        agents[0].DisplayName.ShouldBe("Agent One");
    }

    [Fact]
    public async Task GetConversationsAsync_calls_correct_url_with_agentId()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("/api/conversations", JsonSerializer.Serialize(new[]
        {
            new
            {
                conversationId = "c1",
                agentId = "a1",
                title = "Test",
                isDefault = true,
                status = "Active",
                activeSessionId = (string?)null,
                bindingCount = 0,
                createdAt = DateTimeOffset.UtcNow,
                updatedAt = DateTimeOffset.UtcNow
            }
        }));

        var conversations = await client.GetConversationsAsync("a1");

        handler.LastRequestUrl.ShouldContain("agentId=a1");
        conversations.Count.ShouldBe(1);
        conversations[0].ConversationId.ShouldBe("c1");
    }

    [Fact]
    public async Task GetHistoryAsync_calls_correct_url_with_limit_and_offset()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("/api/conversations/conv1/history", JsonSerializer.Serialize(new
        {
            conversationId = "conv1",
            totalCount = 0,
            offset = 0,
            limit = 50,
            entries = Array.Empty<object>()
        }));

        await client.GetHistoryAsync("conv1", limit: 25, offset: 10);

        handler.LastRequestUrl.ShouldContain("conv1/history");
        handler.LastRequestUrl.ShouldContain("limit=25");
        handler.LastRequestUrl.ShouldContain("offset=10");
    }

    [Fact]
    public async Task GetConversationAsync_calls_correct_url()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("/api/conversations/conv1", JsonSerializer.Serialize(new
        {
            conversationId = "conv1",
            agentId = "a1",
            title = "My Conv",
            isDefault = false,
            status = "Active",
            activeSessionId = (string?)null,
            bindings = Array.Empty<object>(),
            createdAt = DateTimeOffset.UtcNow,
            updatedAt = DateTimeOffset.UtcNow
        }));

        var conv = await client.GetConversationAsync("conv1");

        handler.LastRequestUrl.ShouldContain("conversations/conv1");
        conv.ShouldNotBeNull();
        conv!.ConversationId.ShouldBe("conv1");
    }

    [Fact]
    public void Configure_not_called_throws_on_request()
    {
        var client = new GatewayRestClient(new HttpClient());
        Should.Throw<InvalidOperationException>(() => client.GetAgentsAsync().GetAwaiter().GetResult());
    }
}

/// <summary>Minimal HTTP handler for stubbing REST responses by URL path.</summary>
internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _responses = new();
    public string LastRequestUrl { get; private set; } = string.Empty;

    public void SetResponse(string urlPath, string json)
        => _responses[urlPath] = json;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUrl = request.RequestUri?.ToString() ?? string.Empty;

        foreach (var (path, json) in _responses)
        {
            if (LastRequestUrl.Contains(path))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                });
            }
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

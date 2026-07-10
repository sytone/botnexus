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
    public async Task GetAllConversationsAsync_calls_global_url_without_agentId()
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

        var conversations = await client.GetAllConversationsAsync();

        handler.LastRequestUrl.ShouldEndWith("/api/conversations");
        handler.LastRequestUrl.ShouldNotContain("agentId");
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
    public async Task ArchiveConversationAsync_calls_delete_conversation_endpoint()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("/api/conversations/conv1", "{}");

        var success = await client.ArchiveConversationAsync("conv1");

        success.ShouldBeTrue();
        handler.LastRequestUrl.ShouldContain("/api/conversations/conv1");
    }

    [Fact]
    public async Task ArchiveConversationAsync_encodes_cron_session_colon_id()
    {
        var (client, handler) = CreateClient();
        const string conversationId = "cron-session:cron:20260509002033:6f2f84a4f1634ff492a4fec212872c54";
        handler.SetResponse(Uri.EscapeDataString(conversationId), "{}");

        var success = await client.ArchiveConversationAsync(conversationId);

        success.ShouldBeTrue();
        handler.LastRequestUrl.ShouldContain("/api/conversations/");
        handler.LastRequestUrl.ShouldContain(Uri.EscapeDataString(conversationId));
    }

    [Fact]
    public async Task GetWorkspaceAsync_calls_workspace_root_endpoint()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("/api/agents/agent-1/workspace", JsonSerializer.Serialize(new
        {
            type = "directory",
            path = "",
            entries = Array.Empty<object>()
        }));

        var response = await client.GetWorkspaceAsync("agent-1");

        handler.LastRequestUrl.ShouldContain("/api/agents/agent-1/workspace");
        response.ShouldNotBeNull();
        response!.Type.ShouldBe("directory");
    }

    [Fact]
    public async Task GetWorkspaceAsync_encodes_requested_sub_path()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("/api/agents/agent-1/workspace/memory/notes.md", JsonSerializer.Serialize(new
        {
            type = "text",
            path = "memory/notes.md",
            content = "hello"
        }));

        await client.GetWorkspaceAsync("agent-1", "memory/notes.md");

        handler.LastRequestUrl.ShouldContain("/api/agents/agent-1/workspace/memory/notes.md");
    }

    [Fact]
    public async Task GetReportsAsync_calls_reports_listing_endpoint()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("/api/agents/agent-1/reports", JsonSerializer.Serialize(new
        {
            reports = new[]
            {
                new
                {
                    name = "weekly.md",
                    size = 1204,
                    lastModifiedUtc = DateTimeOffset.UtcNow
                }
            }
        }));

        var reports = await client.GetReportsAsync("agent-1");

        handler.LastRequestUrl.ShouldContain("/api/agents/agent-1/reports");
        reports.Count.ShouldBe(1);
        reports[0].Name.ShouldBe("weekly.md");
    }

    [Fact]
    public async Task GetReportAsync_encodes_report_file_name()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("/api/agents/agent-1/reports/", JsonSerializer.Serialize(new
        {
            name = "weekly summary.md",
            size = 16,
            lastModifiedUtc = DateTimeOffset.UtcNow,
            content = "# Weekly Summary"
        }));

        var response = await client.GetReportAsync("agent-1", "weekly summary.md");

        handler.LastRequestUrl.ShouldContain("/api/agents/agent-1/reports/weekly summary.md");
        response.ShouldNotBeNull();
        response!.Name.ShouldBe("weekly summary.md");
    }

    [Fact]
    public void Configure_not_called_throws_on_request()
    {
        var client = new GatewayRestClient(new HttpClient());
        Should.Throw<InvalidOperationException>(() => client.GetAgentsAsync().GetAwaiter().GetResult());
    }

    // ── DeleteWorkspaceItemAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteWorkspaceItemAsync_calls_correct_url()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("/api/agents/agent-1/workspace/notes.md", "{}");

        var success = await client.DeleteWorkspaceItemAsync("agent-1", "notes.md");

        success.ShouldBeTrue();
        handler.LastRequestUrl.ShouldContain("/api/agents/agent-1/workspace/notes.md");
    }

    [Fact]
    public async Task DeleteWorkspaceItemAsync_appends_force_query_when_true()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("/api/agents/agent-1/workspace/logs", "{}");

        await client.DeleteWorkspaceItemAsync("agent-1", "logs", force: true);

        handler.LastRequestUrl.ShouldContain("?force=true");
    }

    [Fact]
    public async Task DeleteWorkspaceItemAsync_does_not_append_force_when_false()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("/api/agents/agent-1/workspace/notes.md", "{}");

        await client.DeleteWorkspaceItemAsync("agent-1", "notes.md", force: false);

        handler.LastRequestUrl.ShouldNotContain("force");
    }

    [Fact]
    public async Task DeleteWorkspaceItemAsync_encodes_nested_path_segments()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("/api/agents/agent-1/workspace/memory/2026-05-15.md", "{}");

        await client.DeleteWorkspaceItemAsync("agent-1", "memory/2026-05-15.md");

        handler.LastRequestUrl.ShouldContain("/api/agents/agent-1/workspace/memory/2026-05-15.md");
    }

    [Fact]
    public async Task DeleteWorkspaceItemAsync_returns_false_on_error_status()
    {
        var (client, handler) = CreateClient();
        // no registered path => handler returns 404 => IsSuccessStatusCode == false

        var success = await client.DeleteWorkspaceItemAsync("agent-1", "no-such-file.md");

        success.ShouldBeFalse();
    }

    // ── WriteWorkspaceFileAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task WriteWorkspaceFileAsync_calls_correct_url()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("/api/agents/agent-1/workspace/notes.md", "{}");

        var success = await client.WriteWorkspaceFileAsync("agent-1", "notes.md", "# Hello");

        success.ShouldBeTrue();
        handler.LastRequestUrl.ShouldContain("/api/agents/agent-1/workspace/notes.md");
    }

    [Fact]
    public async Task WriteWorkspaceFileAsync_encodes_nested_path_segments()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("/api/agents/agent-1/workspace/memory/today.md", "{}");

        await client.WriteWorkspaceFileAsync("agent-1", "memory/today.md", "content");

        handler.LastRequestUrl.ShouldContain("/api/agents/agent-1/workspace/memory/today.md");
    }

    [Fact]
    public async Task WriteWorkspaceFileAsync_returns_false_on_error_status()
    {
        var (client, handler) = CreateClient();
        // no registered path => handler returns 404 => IsSuccessStatusCode == false

        var success = await client.WriteWorkspaceFileAsync("agent-1", "bad/path/file.md", "content");

        success.ShouldBeFalse();
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

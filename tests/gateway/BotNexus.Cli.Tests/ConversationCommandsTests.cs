using System.Net;
using BotNexus.Cli.Commands;

namespace BotNexus.Cli.Tests;

public sealed class ConversationCommandsTests : IDisposable
{
    private readonly MockHttpServer _server;

    public ConversationCommandsTests()
    {
        _server = new MockHttpServer();
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task List_ReturnsZero_WithValidResponse()
    {
        _server.SetResponse("/api/conversations", HttpStatusCode.OK,
            """[{"conversationId":"c_abc123","agentId":"farnsworth","title":"Test","lastUpdatedUtc":"2026-06-12T00:00:00Z"}]""");

        var result = await ConversationCommands.ExecuteListAsync(_server.BaseUrl, null, "json", CancellationToken.None);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task List_ReturnsZero_WithAgentFilter()
    {
        _server.SetResponse("/api/conversations", HttpStatusCode.OK, "[]");

        var result = await ConversationCommands.ExecuteListAsync(_server.BaseUrl, "farnsworth", "table", CancellationToken.None);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task List_ReturnsOne_WhenGatewayUnreachable()
    {
        var result = await ConversationCommands.ExecuteListAsync("http://localhost:1", null, "table", CancellationToken.None);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Inspect_ReturnsZero_WithValidConversation()
    {
        _server.SetResponse("/api/conversations/c_abc123", HttpStatusCode.OK,
            """{"conversationId":"c_abc123","agentId":"farnsworth","title":"Test","status":"active","createdUtc":"2026-06-12T00:00:00Z","lastUpdatedUtc":"2026-06-12T00:00:00Z","participants":[{"citizenId":"farnsworth"}],"bindings":[]}""");

        var result = await ConversationCommands.ExecuteInspectAsync(_server.BaseUrl, "c_abc123", "json", CancellationToken.None);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task Inspect_ReturnsOne_WhenNotFound()
    {
        _server.SetResponse("/api/conversations/c_missing", HttpStatusCode.NotFound, "");

        var result = await ConversationCommands.ExecuteInspectAsync(_server.BaseUrl, "c_missing", "table", CancellationToken.None);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Inspect_ReturnsOne_WhenGatewayUnreachable()
    {
        var result = await ConversationCommands.ExecuteInspectAsync("http://localhost:1", "c_abc", "table", CancellationToken.None);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Archive_ReturnsZero_WhenSuccessful()
    {
        _server.SetResponse("/api/conversations/c_abc123", HttpStatusCode.NoContent, "");

        var result = await ConversationCommands.ExecuteArchiveAsync(_server.BaseUrl, "c_abc123", CancellationToken.None);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task Archive_ReturnsOne_WhenNotFound()
    {
        _server.SetResponse("/api/conversations/c_missing", HttpStatusCode.NotFound, "");

        var result = await ConversationCommands.ExecuteArchiveAsync(_server.BaseUrl, "c_missing", CancellationToken.None);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Archive_ReturnsOne_WhenGatewayUnreachable()
    {
        var result = await ConversationCommands.ExecuteArchiveAsync("http://localhost:1", "c_abc", CancellationToken.None);

        Assert.Equal(1, result);
    }
}

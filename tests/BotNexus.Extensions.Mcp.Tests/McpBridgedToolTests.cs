using System.Text.Json;
using BotNexus.AgentCore.Types;
using BotNexus.Extensions.Mcp.Protocol;
using FluentAssertions;

namespace BotNexus.Extensions.Mcp.Tests;

public class McpBridgedToolTests
{
    private static (McpClient Client, MockMcpTransport Transport) CreateInitializedClient(string serverId = "github")
    {
        var transport = new MockMcpTransport();
        transport.EnqueueResult(1, new McpInitializeResult
        {
            ProtocolVersion = "2024-11-05",
            Capabilities = new McpServerCapabilities(),
        });

        var client = new McpClient(transport, serverId);
        client.InitializeAsync().GetAwaiter().GetResult();
        return (client, transport);
    }

    [Fact]
    public void Name_IsPrefixed_WhenUsePrefixIsTrue()
    {
        var (client, _) = CreateInitializedClient("github");
        var definition = new McpToolDefinition
        {
            Name = "search_repositories",
            Description = "Search repos",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }),
        };

        var tool = new McpBridgedTool(client, definition, usePrefix: true);

        tool.Name.Should().Be("github_search_repositories");
        tool.Label.Should().Be("search_repositories");
    }

    [Fact]
    public void Name_IsNotPrefixed_WhenUsePrefixIsFalse()
    {
        var (client, _) = CreateInitializedClient("github");
        var definition = new McpToolDefinition
        {
            Name = "search_repositories",
            Description = "Search repos",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }),
        };

        var tool = new McpBridgedTool(client, definition, usePrefix: false);

        tool.Name.Should().Be("search_repositories");
    }

    [Fact]
    public void Definition_ConvertsMcpSchemaToToolDefinition()
    {
        var (client, _) = CreateInitializedClient("test");
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "Search query" },
            },
            required = new[] { "query" },
        });

        var definition = new McpToolDefinition
        {
            Name = "search",
            Description = "Search for things",
            InputSchema = schema,
        };

        var tool = new McpBridgedTool(client, definition);

        tool.Definition.Name.Should().Be("test_search");
        tool.Definition.Description.Should().Be("Search for things");
        tool.Definition.Parameters.GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsTextContent()
    {
        var (client, transport) = CreateInitializedClient("test");

        transport.EnqueueResult(2, new McpToolCallResult
        {
            Content = [new McpContent { Type = "text", Text = "Found 42 results" }],
        });

        var definition = new McpToolDefinition
        {
            Name = "search",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }),
        };
        var tool = new McpBridgedTool(client, definition);

        var args = new Dictionary<string, object?> { ["query"] = "test" };
        var result = await tool.ExecuteAsync("call-1", args);

        result.Content.Should().HaveCount(1);
        result.Content[0].Type.Should().Be(AgentToolContentType.Text);
        result.Content[0].Value.Should().Be("Found 42 results");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsErrorMessage_OnMcpException()
    {
        var (client, transport) = CreateInitializedClient("test");

        transport.EnqueueResponse(new JsonRpcResponse
        {
            Id = 2,
            Error = new JsonRpcError { Code = -32000, Message = "Server error" },
        });

        var definition = new McpToolDefinition
        {
            Name = "broken",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }),
        };
        var tool = new McpBridgedTool(client, definition);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>());

        result.Content.Should().HaveCount(1);
        result.Content[0].Value.Should().Contain("MCP error").And.Contain("Server error");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsNoContent_WhenResultEmpty()
    {
        var (client, transport) = CreateInitializedClient("test");

        transport.EnqueueResult(2, new McpToolCallResult { Content = [] });

        var definition = new McpToolDefinition
        {
            Name = "empty",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }),
        };
        var tool = new McpBridgedTool(client, definition);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>());

        result.Content.Should().HaveCount(1);
        result.Content[0].Value.Should().Be("[no content]");
    }

    [Fact]
    public async Task PrepareArgumentsAsync_PassesThroughArguments()
    {
        var (client, _) = CreateInitializedClient("test");
        var definition = new McpToolDefinition
        {
            Name = "search",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }),
        };
        var tool = new McpBridgedTool(client, definition);

        var args = new Dictionary<string, object?> { ["query"] = "test" };
        var prepared = await tool.PrepareArgumentsAsync(args);

        prepared.Should().BeSameAs(args);
    }
}

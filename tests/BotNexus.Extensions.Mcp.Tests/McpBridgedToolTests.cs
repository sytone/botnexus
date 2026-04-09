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

    [Fact]
    public void Definition_HandlesNoInputSchema()
    {
        var (client, _) = CreateInitializedClient("test");
        var definition = new McpToolDefinition
        {
            Name = "no_params",
            Description = "A tool with no parameters",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
        };

        var tool = new McpBridgedTool(client, definition);

        tool.Definition.Name.Should().Be("test_no_params");
        tool.Definition.Description.Should().Be("A tool with no parameters");
        tool.Definition.Parameters.GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public void Definition_HandlesComplexNestedSchema()
    {
        var (client, _) = CreateInitializedClient("test");
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                config = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string" },
                        options = new
                        {
                            type = "array",
                            items = new { type = "string" },
                        },
                    },
                },
                count = new { type = "integer" },
            },
            required = new[] { "config" },
        });

        var definition = new McpToolDefinition
        {
            Name = "complex_tool",
            Description = "Tool with nested params",
            InputSchema = schema,
        };

        var tool = new McpBridgedTool(client, definition);

        tool.Definition.Parameters.GetProperty("properties")
            .GetProperty("config")
            .GetProperty("type").GetString().Should().Be("object");
        tool.Definition.Parameters.GetProperty("properties")
            .GetProperty("config")
            .GetProperty("properties")
            .GetProperty("options")
            .GetProperty("type").GetString().Should().Be("array");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsMultipleContentItems()
    {
        var (client, transport) = CreateInitializedClient("test");

        transport.EnqueueResult(2, new McpToolCallResult
        {
            Content =
            [
                new McpContent { Type = "text", Text = "First result" },
                new McpContent { Type = "text", Text = "Second result" },
                new McpContent { Type = "text", Text = "Third result" },
            ],
        });

        var definition = new McpToolDefinition
        {
            Name = "multi",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
        };
        var tool = new McpBridgedTool(client, definition);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>());

        result.Content.Should().HaveCount(3);
        result.Content[0].Value.Should().Be("First result");
        result.Content[1].Value.Should().Be("Second result");
        result.Content[2].Value.Should().Be("Third result");
    }

    [Fact]
    public async Task ExecuteAsync_HandlesImageContent()
    {
        var (client, transport) = CreateInitializedClient("test");

        transport.EnqueueResult(2, new McpToolCallResult
        {
            Content =
            [
                new McpContent { Type = "image", Text = "data:image/png;base64,iVBOR..." },
            ],
        });

        var definition = new McpToolDefinition
        {
            Name = "screenshot",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
        };
        var tool = new McpBridgedTool(client, definition);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>());

        result.Content.Should().HaveCount(1);
        result.Content[0].Type.Should().Be(AgentToolContentType.Image);
        result.Content[0].Value.Should().StartWith("data:image/png;base64,");
    }

    [Fact]
    public async Task ExecuteAsync_HandlesMixedContent()
    {
        var (client, transport) = CreateInitializedClient("test");

        transport.EnqueueResult(2, new McpToolCallResult
        {
            Content =
            [
                new McpContent { Type = "text", Text = "Here is the screenshot:" },
                new McpContent { Type = "image", Text = "base64imagedata" },
            ],
        });

        var definition = new McpToolDefinition
        {
            Name = "mixed",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
        };
        var tool = new McpBridgedTool(client, definition);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>());

        result.Content.Should().HaveCount(2);
        result.Content[0].Type.Should().Be(AgentToolContentType.Text);
        result.Content[1].Type.Should().Be(AgentToolContentType.Image);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsUnknownContentTypes()
    {
        var (client, transport) = CreateInitializedClient("test");

        transport.EnqueueResult(2, new McpToolCallResult
        {
            Content =
            [
                new McpContent { Type = "audio", Text = "audio-data" },
            ],
        });

        var definition = new McpToolDefinition
        {
            Name = "audio",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
        };
        var tool = new McpBridgedTool(client, definition);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>());

        // Unknown type is skipped; fallback to [no content]
        result.Content.Should().HaveCount(1);
        result.Content[0].Value.Should().Be("[no content]");
    }

    [Fact]
    public async Task ExecuteAsync_SkipsContentWithNullText()
    {
        var (client, transport) = CreateInitializedClient("test");

        transport.EnqueueResult(2, new McpToolCallResult
        {
            Content =
            [
                new McpContent { Type = "text", Text = null },
            ],
        });

        var definition = new McpToolDefinition
        {
            Name = "null_text",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
        };
        var tool = new McpBridgedTool(client, definition);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>());

        // Null text content is skipped; fallback to [no content]
        result.Content.Should().HaveCount(1);
        result.Content[0].Value.Should().Be("[no content]");
    }

    [Fact]
    public async Task ExecuteAsync_ErrorResult_ReturnsErrorMessage()
    {
        var (client, transport) = CreateInitializedClient("test");

        transport.EnqueueResponse(new JsonRpcResponse
        {
            Id = 2,
            Error = new JsonRpcError { Code = -32602, Message = "Invalid params" },
        });

        var definition = new McpToolDefinition
        {
            Name = "bad_call",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
        };
        var tool = new McpBridgedTool(client, definition);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>());

        result.Content.Should().HaveCount(1);
        result.Content[0].Type.Should().Be(AgentToolContentType.Text);
        result.Content[0].Value.Should().Contain("MCP error");
        result.Content[0].Value.Should().Contain("-32602");
    }

    [Fact]
    public void Name_DefaultsToPrefix()
    {
        var (client, _) = CreateInitializedClient("srv");
        var definition = new McpToolDefinition
        {
            Name = "my_tool",
            InputSchema = default,
        };

        // Default usePrefix is true
        var tool = new McpBridgedTool(client, definition);

        tool.Name.Should().Be("srv_my_tool");
    }

    [Fact]
    public void Definition_UsesEmptyDescription_WhenNull()
    {
        var (client, _) = CreateInitializedClient("test");
        var definition = new McpToolDefinition
        {
            Name = "minimal",
            Description = null,
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
        };

        var tool = new McpBridgedTool(client, definition);

        tool.Definition.Description.Should().BeEmpty();
    }
}

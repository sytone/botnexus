using System.Text.Json;
using BotNexus.Extensions.Mcp.Protocol;
using FluentAssertions;

namespace BotNexus.Extensions.Mcp.Tests;

public class McpTypesTests
{
    [Fact]
    public void McpToolDefinition_Deserializes_FromJson()
    {
        var json = """
        {
            "name": "search_repositories",
            "description": "Search GitHub repositories",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "query": { "type": "string", "description": "Search query" }
                },
                "required": ["query"]
            }
        }
        """;

        var tool = JsonSerializer.Deserialize<McpToolDefinition>(json);

        tool.Should().NotBeNull();
        tool!.Name.Should().Be("search_repositories");
        tool.Description.Should().Be("Search GitHub repositories");
        tool.InputSchema.GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public void McpToolCallResult_Deserializes_TextContent()
    {
        var json = """
        {
            "content": [
                { "type": "text", "text": "Hello, world!" }
            ],
            "isError": false
        }
        """;

        var result = JsonSerializer.Deserialize<McpToolCallResult>(json);

        result.Should().NotBeNull();
        result!.Content.Should().HaveCount(1);
        result.Content[0].Type.Should().Be("text");
        result.Content[0].Text.Should().Be("Hello, world!");
        result.IsError.Should().BeFalse();
    }

    [Fact]
    public void McpToolCallResult_Deserializes_ErrorResult()
    {
        var json = """
        {
            "content": [
                { "type": "text", "text": "Something went wrong" }
            ],
            "isError": true
        }
        """;

        var result = JsonSerializer.Deserialize<McpToolCallResult>(json);

        result.Should().NotBeNull();
        result!.IsError.Should().BeTrue();
    }

    [Fact]
    public void McpInitializeResult_Deserializes_WithCapabilities()
    {
        var json = """
        {
            "protocolVersion": "2024-11-05",
            "capabilities": {
                "tools": { "listChanged": true }
            },
            "serverInfo": {
                "name": "test-server",
                "version": "1.0.0"
            }
        }
        """;

        var result = JsonSerializer.Deserialize<McpInitializeResult>(json);

        result.Should().NotBeNull();
        result!.ProtocolVersion.Should().Be("2024-11-05");
        result.Capabilities.Tools.Should().NotBeNull();
        result.Capabilities.Tools!.ListChanged.Should().BeTrue();
        result.ServerInfo.Should().NotBeNull();
        result.ServerInfo!.Name.Should().Be("test-server");
    }

    [Fact]
    public void McpToolsListResult_Deserializes_MultiplTools()
    {
        var json = """
        {
            "tools": [
                {
                    "name": "tool_a",
                    "description": "Tool A",
                    "inputSchema": { "type": "object", "properties": {} }
                },
                {
                    "name": "tool_b",
                    "description": "Tool B",
                    "inputSchema": { "type": "object", "properties": {} }
                }
            ]
        }
        """;

        var result = JsonSerializer.Deserialize<McpToolsListResult>(json);

        result.Should().NotBeNull();
        result!.Tools.Should().HaveCount(2);
        result.Tools[0].Name.Should().Be("tool_a");
        result.Tools[1].Name.Should().Be("tool_b");
    }

    [Fact]
    public void McpInitializeParams_Serializes_WithDefaults()
    {
        var initParams = new McpInitializeParams();

        var json = JsonSerializer.Serialize(initParams);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("protocolVersion").GetString().Should().Be("2024-11-05");
        doc.RootElement.GetProperty("clientInfo").GetProperty("name").GetString().Should().Be("BotNexus");
    }

    [Fact]
    public void McpToolCallParams_Serializes_WithArguments()
    {
        var callParams = new McpToolCallParams
        {
            Name = "search",
            Arguments = JsonSerializer.SerializeToElement(new { query = "test" }),
        };

        var json = JsonSerializer.Serialize(callParams);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("name").GetString().Should().Be("search");
        doc.RootElement.GetProperty("arguments").GetProperty("query").GetString().Should().Be("test");
    }
}

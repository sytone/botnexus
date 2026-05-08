using System.Collections.Concurrent;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Extensions.Mcp;
using BotNexus.Extensions.Mcp.Protocol;

namespace BotNexus.Extensions.McpInvoke.Tests;

/// <summary>
/// Integration tests for McpInvokeTool that exercise the full call/list_tools flow using MockMcpTransport.
/// </summary>
public class McpInvokeToolIntegrationTests
{
    private static MockMcpTransport CreateMockTransportWithInitResponse()
    {
        var transport = new MockMcpTransport();

        var initResult = new McpInitializeResult
        {
            ProtocolVersion = "2024-11-05",
            Capabilities = new McpServerCapabilities
            {
                Tools = new McpToolCapability { ListChanged = false },
            },
            ServerInfo = new McpServerInfo { Name = "test-server", Version = "1.0" },
        };

        transport.EnqueueResult(1, initResult);
        return transport;
    }

    private static async Task<McpInvokeTool> CreateToolWithMockClient(string serverId, MockMcpTransport transport)
    {
        var config = new McpInvokeConfig
        {
            Servers = new Dictionary<string, McpServerConfig>
            {
                [serverId] = new McpServerConfig
                {
                    Command = "mock",
                    Args = [],
                    InitTimeoutMs = 5000,
                    CallTimeoutMs = 30000,
                }
            }
        };

        var client = new McpClient(transport, serverId);
        await client.InitializeAsync();

        var clientsDict = new ConcurrentDictionary<string, McpClient>(StringComparer.OrdinalIgnoreCase);
        clientsDict[serverId] = client;

        return new McpInvokeTool(config, clientsDict);
    }

    [Fact]
    public async Task ListTools_ReturnsToolNames()
    {
        var transport = CreateMockTransportWithInitResponse();

        var toolsResult = new McpToolsListResult
        {
            Tools = [
                new McpToolDefinition
                {
                    Name = "search",
                    Description = "Search for things",
                    InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                },
                new McpToolDefinition
                {
                    Name = "create",
                    Description = "Create a resource",
                    InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                },
            ],
        };
        transport.EnqueueResult(2, toolsResult);

        var tool = await CreateToolWithMockClient("test-server", transport);

        var args = new Dictionary<string, object?>
        {
            ["action"] = "list_tools",
            ["server"] = "test-server"
        };
        var result = await tool.ExecuteAsync("test-call", args);

        result.Content.Count().ShouldBe(1);
        result.Content[0].Type.ShouldBe(AgentToolContentType.Text);
        
        var text = result.Content[0].Value;
        text.ShouldContain("Tools on 'test-server'");
        text.ShouldContain("search");
        text.ShouldContain("Search for things");
        text.ShouldContain("create");
        text.ShouldContain("Create a resource");

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task Call_ReturnsTextContent()
    {
        var transport = CreateMockTransportWithInitResponse();

        var callResult = new McpToolCallResult
        {
            Content = [new McpContent { Type = "text", Text = "Search results here" }],
            IsError = false,
        };
        transport.EnqueueResult(2, callResult);

        var tool = await CreateToolWithMockClient("test-server", transport);

        var args = new Dictionary<string, object?>
        {
            ["action"] = "call",
            ["server"] = "test-server",
            ["tool"] = "search",
            ["arguments"] = new Dictionary<string, object?> { ["query"] = "hello" }
        };
        var result = await tool.ExecuteAsync("test-call", args);

        result.Content.Count().ShouldBe(1);
        result.Content[0].Type.ShouldBe(AgentToolContentType.Text);
        result.Content[0].Value.ShouldBe("Search results here");

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task Call_ReturnsImageContent()
    {
        var transport = CreateMockTransportWithInitResponse();

        var callResult = new McpToolCallResult
        {
            Content = [new McpContent { Type = "image", Text = "data:image/png;base64,abc123" }],
            IsError = false,
        };
        transport.EnqueueResult(2, callResult);

        var tool = await CreateToolWithMockClient("test-server", transport);

        var args = new Dictionary<string, object?>
        {
            ["action"] = "call",
            ["server"] = "test-server",
            ["tool"] = "screenshot",
        };
        var result = await tool.ExecuteAsync("test-call", args);

        result.Content.Count().ShouldBe(1);
        result.Content[0].Type.ShouldBe(AgentToolContentType.Image);
        result.Content[0].Value.ShouldBe("data:image/png;base64,abc123");

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task Call_WithMcpError_ReturnsErrorMessage()
    {
        var transport = CreateMockTransportWithInitResponse();

        transport.EnqueueResponse(new JsonRpcResponse
        {
            Id = 2,
            Error = new JsonRpcError { Code = -32000, Message = "Tool execution failed" },
        });

        var tool = await CreateToolWithMockClient("test-server", transport);

        var args = new Dictionary<string, object?>
        {
            ["action"] = "call",
            ["server"] = "test-server",
            ["tool"] = "broken",
        };
        var result = await tool.ExecuteAsync("test-call", args);

        result.Content.Count().ShouldBe(1);
        result.Content[0].Type.ShouldBe(AgentToolContentType.Text);
        result.Content[0].Value.ShouldContain("MCP error");
        result.Content[0].Value.ShouldContain("-32000");
        result.Content[0].Value.ShouldContain("Tool execution failed");

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task Call_WithMultipleContentBlocks_ReturnsAllBlocks()
    {
        var transport = CreateMockTransportWithInitResponse();

        var callResult = new McpToolCallResult
        {
            Content =
            [
                new McpContent { Type = "text", Text = "First block" },
                new McpContent { Type = "text", Text = "Second block" },
                new McpContent { Type = "image", Text = "data:image/png;base64,img1" },
            ],
            IsError = false,
        };
        transport.EnqueueResult(2, callResult);

        var tool = await CreateToolWithMockClient("test-server", transport);

        var args = new Dictionary<string, object?>
        {
            ["action"] = "call",
            ["server"] = "test-server",
            ["tool"] = "multi",
        };
        var result = await tool.ExecuteAsync("test-call", args);

        result.Content.Count().ShouldBe(3);
        result.Content[0].Type.ShouldBe(AgentToolContentType.Text);
        result.Content[0].Value.ShouldBe("First block");
        result.Content[1].Type.ShouldBe(AgentToolContentType.Text);
        result.Content[1].Value.ShouldBe("Second block");
        result.Content[2].Type.ShouldBe(AgentToolContentType.Image);
        result.Content[2].Value.ShouldBe("data:image/png;base64,img1");

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task Call_WithEmptyContent_ReturnsNoContentPlaceholder()
    {
        var transport = CreateMockTransportWithInitResponse();

        var callResult = new McpToolCallResult
        {
            Content = [],
            IsError = false,
        };
        transport.EnqueueResult(2, callResult);

        var tool = await CreateToolWithMockClient("test-server", transport);

        var args = new Dictionary<string, object?>
        {
            ["action"] = "call",
            ["server"] = "test-server",
            ["tool"] = "empty",
        };
        var result = await tool.ExecuteAsync("test-call", args);

        result.Content.Count().ShouldBe(1);
        result.Content[0].Type.ShouldBe(AgentToolContentType.Text);
        result.Content[0].Value.ShouldBe("[no content]");

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionCaching_ReusesSameClient()
    {
        var transport = CreateMockTransportWithInitResponse();

        var toolsResult = new McpToolsListResult { Tools = [] };
        transport.EnqueueResult(2, toolsResult);
        transport.EnqueueResult(3, toolsResult);

        var tool = await CreateToolWithMockClient("test-server", transport);

        var args = new Dictionary<string, object?>
        {
            ["action"] = "list_tools",
            ["server"] = "test-server"
        };

        await tool.ExecuteAsync("call-1", args);
        await tool.ExecuteAsync("call-2", args);

        transport.SentRequests.Count().ShouldBe(3);
        transport.SentRequests[0].Method.ShouldBe("initialize");
        transport.SentRequests[1].Method.ShouldBe("tools/list");
        transport.SentRequests[2].Method.ShouldBe("tools/list");

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_DisposesAllCachedClients()
    {
        var transport = CreateMockTransportWithInitResponse();
        var toolsResult = new McpToolsListResult { Tools = [] };
        transport.EnqueueResult(2, toolsResult);

        var tool = await CreateToolWithMockClient("test-server", transport);

        var args = new Dictionary<string, object?>
        {
            ["action"] = "list_tools",
            ["server"] = "test-server"
        };
        await tool.ExecuteAsync("call-1", args);

        await tool.DisposeAsync();

        transport.SentRequests.Count().ShouldBe(2);
    }
}

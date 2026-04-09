using System.Text.Json;
using BotNexus.Extensions.Mcp.Protocol;
using FluentAssertions;

namespace BotNexus.Extensions.Mcp.Tests;

public class McpClientTests
{
    private static MockMcpTransport CreateTransportWithInitResponse()
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

    [Fact]
    public async Task InitializeAsync_SendsInitializeRequest_And_InitializedNotification()
    {
        var transport = CreateTransportWithInitResponse();
        var client = new McpClient(transport, "test");

        await client.InitializeAsync();

        transport.SentRequests.Should().HaveCount(1);
        transport.SentRequests[0].Method.Should().Be("initialize");

        transport.SentNotifications.Should().HaveCount(1);
        transport.SentNotifications[0].Method.Should().Be("notifications/initialized");

        client.Capabilities.Should().NotBeNull();
        client.Capabilities!.Tools.Should().NotBeNull();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task InitializeAsync_ThrowsMcpException_OnErrorResponse()
    {
        var transport = new MockMcpTransport();
        transport.EnqueueResponse(new JsonRpcResponse
        {
            Id = 1,
            Error = new JsonRpcError { Code = -32600, Message = "Invalid request" },
        });

        var client = new McpClient(transport, "test");

        var act = () => client.InitializeAsync();
        await act.Should().ThrowAsync<McpException>()
            .WithMessage("*Invalid request*");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ListToolsAsync_ReturnsToolDefinitions()
    {
        var transport = CreateTransportWithInitResponse();

        var toolsResult = new McpToolsListResult
        {
            Tools = [
                new McpToolDefinition
                {
                    Name = "search",
                    Description = "Search things",
                    InputSchema = JsonSerializer.SerializeToElement(new
                    {
                        type = "object",
                        properties = new { query = new { type = "string" } },
                    }),
                },
            ],
        };
        transport.EnqueueResult(2, toolsResult);

        var client = new McpClient(transport, "github");
        await client.InitializeAsync();

        var tools = await client.ListToolsAsync();

        tools.Should().HaveCount(1);
        tools[0].Name.Should().Be("search");
        tools[0].Description.Should().Be("Search things");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ListToolsAsync_ThrowsIfNotInitialized()
    {
        var transport = new MockMcpTransport();
        var client = new McpClient(transport, "test");

        var act = () => client.ListToolsAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not been initialized*");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task CallToolAsync_SendsRequest_And_ReturnsResult()
    {
        var transport = CreateTransportWithInitResponse();

        var callResult = new McpToolCallResult
        {
            Content = [new McpContent { Type = "text", Text = "Result text" }],
            IsError = false,
        };
        transport.EnqueueResult(2, callResult);

        var client = new McpClient(transport, "test");
        await client.InitializeAsync();

        var args = JsonSerializer.SerializeToElement(new { query = "hello" });
        var result = await client.CallToolAsync("search", args);

        result.Content.Should().HaveCount(1);
        result.Content[0].Text.Should().Be("Result text");
        result.IsError.Should().BeFalse();

        transport.SentRequests.Should().HaveCount(2);
        transport.SentRequests[1].Method.Should().Be("tools/call");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task CallToolAsync_ThrowsMcpException_OnError()
    {
        var transport = CreateTransportWithInitResponse();
        transport.EnqueueResponse(new JsonRpcResponse
        {
            Id = 2,
            Error = new JsonRpcError { Code = -32000, Message = "Tool failed" },
        });

        var client = new McpClient(transport, "test");
        await client.InitializeAsync();

        var act = () => client.CallToolAsync("broken_tool");
        await act.Should().ThrowAsync<McpException>()
            .WithMessage("*Tool failed*");

        await client.DisposeAsync();
    }

    [Fact]
    public void ServerId_ReturnsConfiguredId()
    {
        var transport = new MockMcpTransport();
        var client = new McpClient(transport, "my-server");

        client.ServerId.Should().Be("my-server");
    }
}

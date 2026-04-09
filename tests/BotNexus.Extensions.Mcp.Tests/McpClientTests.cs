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

    [Fact]
    public async Task InitializeAsync_WithNoCapabilities_SetsCapabilitiesToDefaults()
    {
        var transport = new MockMcpTransport();
        transport.EnqueueResult(1, new McpInitializeResult
        {
            ProtocolVersion = "2024-11-05",
            Capabilities = new McpServerCapabilities(),
        });

        var client = new McpClient(transport, "bare");
        await client.InitializeAsync();

        client.Capabilities.Should().NotBeNull();
        client.Capabilities!.Tools.Should().BeNull();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ListToolsAsync_ReturnsEmptyList_WhenServerHasNoTools()
    {
        var transport = CreateTransportWithInitResponse();
        transport.EnqueueResult(2, new McpToolsListResult { Tools = [] });

        var client = new McpClient(transport, "empty");
        await client.InitializeAsync();

        var tools = await client.ListToolsAsync();

        tools.Should().BeEmpty();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ListToolsAsync_ReturnsEmptyList_WhenResultHasNoToolsField()
    {
        var transport = CreateTransportWithInitResponse();
        // Response with empty result (no tools property)
        transport.EnqueueResponse(new JsonRpcResponse
        {
            Id = 2,
            Result = JsonSerializer.SerializeToElement(new { }),
        });

        var client = new McpClient(transport, "empty");
        await client.InitializeAsync();

        var tools = await client.ListToolsAsync();

        tools.Should().BeEmpty();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task CallToolAsync_ThrowsIfNotInitialized()
    {
        var transport = new MockMcpTransport();
        var client = new McpClient(transport, "test");

        var act = () => client.CallToolAsync("any_tool");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not been initialized*");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task CallToolAsync_WithTimeout_ThrowsOperationCanceled()
    {
        var transport = CreateTransportWithInitResponse();
        // Don't enqueue a response — ReceiveAsync will block

        var client = new McpClient(transport, "slow");
        await client.InitializeAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => client.CallToolAsync("slow_tool", ct: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task CallToolAsync_ConcurrentCalls_AllResolve()
    {
        var transport = CreateTransportWithInitResponse();

        // Enqueue 3 responses for 3 concurrent calls
        transport.EnqueueResult(2, new McpToolCallResult
        {
            Content = [new McpContent { Type = "text", Text = "result-1" }],
        });
        transport.EnqueueResult(3, new McpToolCallResult
        {
            Content = [new McpContent { Type = "text", Text = "result-2" }],
        });
        transport.EnqueueResult(4, new McpToolCallResult
        {
            Content = [new McpContent { Type = "text", Text = "result-3" }],
        });

        var client = new McpClient(transport, "multi");
        await client.InitializeAsync();

        var task1 = client.CallToolAsync("tool1");
        var task2 = client.CallToolAsync("tool2");
        var task3 = client.CallToolAsync("tool3");

        var results = await Task.WhenAll(task1, task2, task3);

        results.Should().HaveCount(3);
        var texts = results.SelectMany(r => r.Content).Select(c => c.Text).OrderBy(t => t).ToList();
        texts.Should().Contain("result-1");
        texts.Should().Contain("result-2");
        texts.Should().Contain("result-3");

        transport.SentRequests.Should().HaveCount(4); // 1 init + 3 calls

        await client.DisposeAsync();
    }

    [Fact]
    public async Task CallToolAsync_WithNoArguments_SendsNullArguments()
    {
        var transport = CreateTransportWithInitResponse();
        transport.EnqueueResult(2, new McpToolCallResult
        {
            Content = [new McpContent { Type = "text", Text = "ok" }],
        });

        var client = new McpClient(transport, "test");
        await client.InitializeAsync();

        var result = await client.CallToolAsync("no_args_tool");

        result.Content.Should().HaveCount(1);
        result.Content[0].Text.Should().Be("ok");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task CallToolAsync_ReturnsEmptyResult_WhenNoResultField()
    {
        var transport = CreateTransportWithInitResponse();
        transport.EnqueueResponse(new JsonRpcResponse { Id = 2 });

        var client = new McpClient(transport, "test");
        await client.InitializeAsync();

        var result = await client.CallToolAsync("void_tool");

        result.Content.Should().BeEmpty();
        result.IsError.Should().BeFalse();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ListToolsAsync_ThrowsMcpException_OnErrorResponse()
    {
        var transport = CreateTransportWithInitResponse();
        transport.EnqueueResponse(new JsonRpcResponse
        {
            Id = 2,
            Error = new JsonRpcError { Code = -32601, Message = "Method not found" },
        });

        var client = new McpClient(transport, "test");
        await client.InitializeAsync();

        var act = () => client.ListToolsAsync();
        await act.Should().ThrowAsync<McpException>()
            .WithMessage("*Method not found*");

        await client.DisposeAsync();
    }

    [Fact]
    public void Capabilities_IsNull_BeforeInitialize()
    {
        var transport = new MockMcpTransport();
        var client = new McpClient(transport, "test");

        client.Capabilities.Should().BeNull();
    }

    [Fact]
    public async Task McpException_PreservesErrorCode()
    {
        var transport = CreateTransportWithInitResponse();
        transport.EnqueueResponse(new JsonRpcResponse
        {
            Id = 2,
            Error = new JsonRpcError { Code = -32001, Message = "Custom error" },
        });

        var client = new McpClient(transport, "test");
        await client.InitializeAsync();

        McpException? caught = null;
        try
        {
            await client.CallToolAsync("fail");
        }
        catch (McpException ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull();
        caught!.Code.Should().Be(-32001);
        caught.Message.Should().Contain("Custom error");

        await client.DisposeAsync();
    }
}

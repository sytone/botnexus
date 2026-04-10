using System.Text.Json;
using BotNexus.Extensions.Mcp.Protocol;
using BotNexus.Extensions.Mcp.Transport;
using FluentAssertions;

namespace BotNexus.Extensions.Mcp.Tests.Security;

public sealed class McpClientSecurityTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task ToolNameWithInjectionCharacters_IsSerializedAsData()
    {
        var transport = CreateInitializedTransport();
        transport.EnqueueResult(2, new McpToolCallResult { Content = [new McpContent { Type = "text", Text = "ok" }] });
        var client = new McpClient(transport, "test");
        await client.InitializeAsync();

        var _ = await client.CallToolAsync("\"; DROP TABLE");

        var callRequest = transport.SentRequests[1];
        callRequest.Method.Should().Be("tools/call");
        callRequest.Params.Should().NotBeNull();
        callRequest.Params!.Value.GetProperty("name").GetString().Should().Be("\"; DROP TABLE");
        await client.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task VeryLargeToolArguments_AreAcceptedWithoutExplicitSizeGuard_CurrentBehavior()
    {
        var transport = CreateInitializedTransport();
        transport.EnqueueResult(2, new McpToolCallResult());
        var client = new McpClient(transport, "test");
        await client.InitializeAsync();

        var args = JsonSerializer.SerializeToElement(new { payload = new string('x', 5 * 1024 * 1024) });
        var result = await client.CallToolAsync("large", args);
        result.Should().NotBeNull();
        await client.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task MalformedTransportJsonException_BubblesWithoutFriendlyWrap_CurrentBehavior()
    {
        var transport = new ThrowingTransport(new JsonException("malformed"));
        var client = new McpClient(transport, "test");

        var act = () => client.InitializeAsync();
        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task InitializeResponseMissingRequiredFields_IsNotValidated_CurrentBehavior()
    {
        var transport = new MockMcpTransport();
        transport.EnqueueResponse(new JsonRpcResponse
        {
            Id = 1,
            Result = JsonSerializer.SerializeToElement(new { capabilities = new { } })
        });
        var client = new McpClient(transport, "test");

        await client.InitializeAsync();
        client.Capabilities.Should().NotBeNull();
        await client.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ServerErrorResult_ThrowsMcpExceptionWithDetails()
    {
        var transport = CreateInitializedTransport();
        transport.EnqueueResponse(new JsonRpcResponse { Id = 2, Error = new JsonRpcError { Code = -32000, Message = "denied" } });
        var client = new McpClient(transport, "test");
        await client.InitializeAsync();

        var act = () => client.CallToolAsync("secret");
        await act.Should().ThrowAsync<McpException>().WithMessage("*denied*");
        await client.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task DuplicateToolNames_AreReturnedAsIs_CurrentBehavior()
    {
        var transport = CreateInitializedTransport();
        transport.EnqueueResult(2, new McpToolsListResult
        {
            Tools =
            [
                new McpToolDefinition { Name = "dup", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) },
                new McpToolDefinition { Name = "dup", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }
            ]
        });
        var client = new McpClient(transport, "test");
        await client.InitializeAsync();

        var tools = await client.ListToolsAsync();
        tools.Count(t => t.Name == "dup").Should().Be(2);
        await client.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task BinaryStyleToolResultContent_IsNotRejected_CurrentBehavior()
    {
        var transport = CreateInitializedTransport();
        transport.EnqueueResponse(new JsonRpcResponse
        {
            Id = 2,
            Result = JsonSerializer.SerializeToElement(new
            {
                content = new[] { new { type = "image", text = (string?)null } },
                isError = false
            })
        });
        var client = new McpClient(transport, "test");
        await client.InitializeAsync();

        var result = await client.CallToolAsync("binary");
        result.Content.Should().ContainSingle();
        result.Content[0].Type.Should().Be("image");
        await client.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ListToolsErrorResponse_ThrowsMcpException()
    {
        var transport = CreateInitializedTransport();
        transport.EnqueueResponse(new JsonRpcResponse { Id = 2, Error = new JsonRpcError { Code = -32601, Message = "blocked" } });
        var client = new McpClient(transport, "test");
        await client.InitializeAsync();

        var act = () => client.ListToolsAsync();
        await act.Should().ThrowAsync<McpException>().WithMessage("*blocked*");
        await client.DisposeAsync();
    }

    private static MockMcpTransport CreateInitializedTransport()
    {
        var transport = new MockMcpTransport();
        transport.EnqueueResult(1, new McpInitializeResult
        {
            ProtocolVersion = "2024-11-05",
            Capabilities = new McpServerCapabilities()
        });
        return transport;
    }

    private sealed class ThrowingTransport(Exception toThrow) : IMcpTransport
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(JsonRpcRequest message, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendNotificationAsync(JsonRpcNotification message, CancellationToken ct = default) => Task.CompletedTask;
        public Task<JsonRpcResponse> ReceiveAsync(CancellationToken ct = default) => Task.FromException<JsonRpcResponse>(toThrow);
    }
}

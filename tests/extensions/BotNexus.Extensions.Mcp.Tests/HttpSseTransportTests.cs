using System.Text;
using System.Text.Json;
using BotNexus.Extensions.Mcp.Protocol;
using BotNexus.Extensions.Mcp.Transport;

namespace BotNexus.Extensions.Mcp.Tests;

public class HttpSseTransportTests
{
    [Fact]
    public async Task ParseSseStream_ParsesSingleMessageEvent()
    {
        var response = new JsonRpcResponse
        {
            Id = 1,
            Result = JsonSerializer.SerializeToElement(new { ok = true }),
        };
        var sseData = BuildSseBlock("message", JsonSerializer.Serialize(response, JsonContext.Default.JsonRpcResponse));

        var transport = CreateTransport();
        var reader = new StringReader(sseData);

        await transport.ParseSseStreamAsync(reader, CancellationToken.None);

        var received = await ReadOneResponse(transport);
        received.Id.ShouldNotBeNull();
    }

    [Fact]
    public async Task ParseSseStream_ParsesEventWithoutExplicitType()
    {
        // Per SSE spec, events without an explicit type default to "message"
        var response = new JsonRpcResponse
        {
            Id = 2,
            Result = JsonSerializer.SerializeToElement(new { value = 42 }),
        };
        var json = JsonSerializer.Serialize(response, JsonContext.Default.JsonRpcResponse);
        var sseData = $"data: {json}\n\n";

        var transport = CreateTransport();
        var reader = new StringReader(sseData);

        await transport.ParseSseStreamAsync(reader, CancellationToken.None);

        var received = await ReadOneResponse(transport);
        received.Id.ShouldNotBeNull();
    }

    [Fact]
    public async Task ParseSseStream_IgnoresNonMessageEvents()
    {
        var sseData = "event: ping\ndata: {}\n\n";

        var transport = CreateTransport();
        var reader = new StringReader(sseData);

        await transport.ParseSseStreamAsync(reader, CancellationToken.None);

        // Channel should be empty — no response for non-message events
        var act = () => transport.ReceiveAsync(new CancellationTokenSource(50).Token);
        await act.ShouldThrowAsync<Exception>();
    }

    [Fact]
    public async Task ParseSseStream_HandlesMultipleEvents()
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= 3; i++)
        {
            var response = new JsonRpcResponse
            {
                Id = i,
                Result = JsonSerializer.SerializeToElement(new { index = i }),
            };
            sb.Append(BuildSseBlock("message", JsonSerializer.Serialize(response, JsonContext.Default.JsonRpcResponse)));
        }

        var transport = CreateTransport();
        var reader = new StringReader(sb.ToString());

        await transport.ParseSseStreamAsync(reader, CancellationToken.None);

        var r1 = await ReadOneResponse(transport);
        var r2 = await ReadOneResponse(transport);
        var r3 = await ReadOneResponse(transport);

        r1.ShouldNotBeNull();
        r2.ShouldNotBeNull();
        r3.ShouldNotBeNull();
    }

    [Fact]
    public async Task ParseSseStream_SkipsMalformedJson()
    {
        var validResponse = new JsonRpcResponse
        {
            Id = 1,
            Result = JsonSerializer.SerializeToElement(new { ok = true }),
        };
        var validJson = JsonSerializer.Serialize(validResponse, JsonContext.Default.JsonRpcResponse);

        var sseData = "event: message\ndata: {not valid json\n\n" +
                      $"event: message\ndata: {validJson}\n\n";

        var transport = CreateTransport();
        var reader = new StringReader(sseData);

        await transport.ParseSseStreamAsync(reader, CancellationToken.None);

        var received = await ReadOneResponse(transport);
        received.Id.ShouldNotBeNull();
    }

    [Fact]
    public async Task ParseSseStream_HandlesMultiLineData()
    {
        var response = new JsonRpcResponse
        {
            Id = 5,
            Result = JsonSerializer.SerializeToElement(new { ok = true }),
        };
        var json = JsonSerializer.Serialize(response, JsonContext.Default.JsonRpcResponse);

        // Split JSON across two data: lines — parser concatenates with \n
        var half = json.Length / 2;
        var part1 = json[..half];
        var part2 = json[half..];
        var sseData = $"event: message\ndata: {part1}\ndata: {part2}\n\n";

        var transport = CreateTransport();
        var reader = new StringReader(sseData);

        // Should not throw even if concatenated data is invalid JSON
        await transport.ParseSseStreamAsync(reader, CancellationToken.None);
    }

    [Fact]
    public async Task ParseSseStream_IgnoresCommentLines()
    {
        var response = new JsonRpcResponse
        {
            Id = 6,
            Result = JsonSerializer.SerializeToElement(new { ok = true }),
        };
        var json = JsonSerializer.Serialize(response, JsonContext.Default.JsonRpcResponse);
        var sseData = $": this is a comment\nevent: message\ndata: {json}\n\n";

        var transport = CreateTransport();
        var reader = new StringReader(sseData);

        await transport.ParseSseStreamAsync(reader, CancellationToken.None);

        var received = await ReadOneResponse(transport);
        received.Id.ShouldNotBeNull();
    }

    [Fact]
    public async Task ParseSseStream_HandlesTrailingEventWithoutBlankLine()
    {
        var response = new JsonRpcResponse
        {
            Id = 7,
            Result = JsonSerializer.SerializeToElement(new { ok = true }),
        };
        var json = JsonSerializer.Serialize(response, JsonContext.Default.JsonRpcResponse);
        // No trailing \n\n — stream just ends
        var sseData = $"data: {json}";

        var transport = CreateTransport();
        var reader = new StringReader(sseData);

        await transport.ParseSseStreamAsync(reader, CancellationToken.None);

        var received = await ReadOneResponse(transport);
        received.Id.ShouldNotBeNull();
    }

    [Fact]
    public void Constructor_ThrowsOnNullEndpoint()
    {
        var act = () => new HttpSseMcpTransport(null!);
        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public async Task SendAsync_ThrowsWhenNotConnected()
    {
        var transport = CreateTransport();
        var request = new JsonRpcRequest { Method = "test" };

        var act = () => transport.SendAsync(request);
        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("not connected");
    }

    [Fact]
    public async Task SendNotificationAsync_ThrowsWhenNotConnected()
    {
        var transport = CreateTransport();
        var notification = new JsonRpcNotification { Method = "test" };

        var act = () => transport.SendNotificationAsync(notification);
        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("not connected");
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        var transport = CreateTransport();
        await transport.DisposeAsync();
        await transport.DisposeAsync();
        // Should not throw
    }

    [Fact]
    public async Task ReceiveAsync_ThrowsAfterDispose()
    {
        var transport = CreateTransport();
        await transport.DisposeAsync();

        var act = () => transport.ReceiveAsync();
        await act.ShouldThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task ParseSseStream_HandlesErrorResponse()
    {
        var response = new JsonRpcResponse
        {
            Id = 10,
            Error = new JsonRpcError { Code = -32600, Message = "Invalid request" },
        };
        var json = JsonSerializer.Serialize(response, JsonContext.Default.JsonRpcResponse);
        var sseData = BuildSseBlock("message", json);

        var transport = CreateTransport();
        await transport.ParseSseStreamAsync(new StringReader(sseData), CancellationToken.None);

        var received = await ReadOneResponse(transport);
        received.Error.ShouldNotBeNull();
        received.Error!.Code.ShouldBe(-32600);
    }

    [Fact]
    public async Task ParseSseStream_HandlesEmptyDataField()
    {
        // Empty data field should be skipped (empty string doesn't parse to valid JSON-RPC)
        var sseData = "event: message\ndata:\n\n";

        var transport = CreateTransport();
        await transport.ParseSseStreamAsync(new StringReader(sseData), CancellationToken.None);

        // Should not have enqueued anything (empty string is not valid JSON)
        var act = () => transport.ReceiveAsync(new CancellationTokenSource(50).Token);
        await act.ShouldThrowAsync<Exception>();
    }

    private static HttpSseMcpTransport CreateTransport()
    {
        return new HttpSseMcpTransport(
            new Uri("http://localhost:9999/mcp"),
            connectTimeout: TimeSpan.FromSeconds(5));
    }

    private static string BuildSseBlock(string eventType, string data)
    {
        return $"event: {eventType}\ndata: {data}\n\n";
    }

    private static async Task<JsonRpcResponse> ReadOneResponse(HttpSseMcpTransport transport)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return await transport.ReceiveAsync(cts.Token);
    }
}

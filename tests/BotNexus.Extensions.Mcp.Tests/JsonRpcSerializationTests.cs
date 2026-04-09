using System.Text.Json;
using BotNexus.Extensions.Mcp.Protocol;
using FluentAssertions;

namespace BotNexus.Extensions.Mcp.Tests;

public class JsonRpcSerializationTests
{
    [Fact]
    public void JsonRpcRequest_Serializes_WithCorrectStructure()
    {
        var request = new JsonRpcRequest
        {
            Id = 1,
            Method = "initialize",
            Params = JsonSerializer.SerializeToElement(new { protocolVersion = "2024-11-05" }),
        };

        var json = JsonSerializer.Serialize(request);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        doc.RootElement.GetProperty("id").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("method").GetString().Should().Be("initialize");
        doc.RootElement.GetProperty("params").GetProperty("protocolVersion").GetString().Should().Be("2024-11-05");
    }

    [Fact]
    public void JsonRpcRequest_Serializes_WithoutNullParams()
    {
        var request = new JsonRpcRequest
        {
            Id = 1,
            Method = "tools/list",
        };

        var json = JsonSerializer.Serialize(request);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("params", out _).Should().BeFalse();
    }

    [Fact]
    public void JsonRpcResponse_Deserializes_SuccessResponse()
    {
        var json = """
        {
            "jsonrpc": "2.0",
            "id": 1,
            "result": {
                "protocolVersion": "2024-11-05",
                "capabilities": { "tools": { "listChanged": true } },
                "serverInfo": { "name": "test-server", "version": "1.0" }
            }
        }
        """;

        var response = JsonSerializer.Deserialize<JsonRpcResponse>(json);

        response.Should().NotBeNull();
        response!.Id.Should().NotBeNull();
        response.Result.Should().NotBeNull();
        response.Error.Should().BeNull();
    }

    [Fact]
    public void JsonRpcResponse_Deserializes_ErrorResponse()
    {
        var json = """
        {
            "jsonrpc": "2.0",
            "id": 1,
            "error": {
                "code": -32601,
                "message": "Method not found"
            }
        }
        """;

        var response = JsonSerializer.Deserialize<JsonRpcResponse>(json);

        response.Should().NotBeNull();
        response!.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(-32601);
        response.Error.Message.Should().Be("Method not found");
        response.Result.Should().BeNull();
    }

    [Fact]
    public void JsonRpcNotification_Serializes_WithoutId()
    {
        var notification = new JsonRpcNotification
        {
            Method = "notifications/initialized",
        };

        var json = JsonSerializer.Serialize(notification);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        doc.RootElement.GetProperty("method").GetString().Should().Be("notifications/initialized");
        doc.RootElement.TryGetProperty("id", out _).Should().BeFalse();
    }
}

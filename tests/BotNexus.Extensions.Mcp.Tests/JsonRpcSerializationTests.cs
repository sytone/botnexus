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

    [Fact]
    public void JsonRpcResponse_Deserializes_MissingId()
    {
        var json = """
        {
            "jsonrpc": "2.0",
            "result": { "value": 1 }
        }
        """;

        var response = JsonSerializer.Deserialize<JsonRpcResponse>(json);

        response.Should().NotBeNull();
        response!.Id.Should().BeNull();
        response.Result.Should().NotBeNull();
    }

    [Fact]
    public void JsonRpcResponse_Deserializes_WrongVersion()
    {
        var json = """
        {
            "jsonrpc": "1.0",
            "id": 1,
            "result": {}
        }
        """;

        var response = JsonSerializer.Deserialize<JsonRpcResponse>(json);

        response.Should().NotBeNull();
        response!.Jsonrpc.Should().Be("1.0");
    }

    [Fact]
    public void JsonRpcResponse_Deserializes_ErrorWithDataField()
    {
        var json = """
        {
            "jsonrpc": "2.0",
            "id": 1,
            "error": {
                "code": -32603,
                "message": "Internal error",
                "data": { "detail": "stack trace here" }
            }
        }
        """;

        var response = JsonSerializer.Deserialize<JsonRpcResponse>(json);

        response.Should().NotBeNull();
        response!.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(-32603);
        response.Error.Message.Should().Be("Internal error");
        response.Error.Data.Should().NotBeNull();
        response.Error.Data!.Value.GetProperty("detail").GetString().Should().Be("stack trace here");
    }

    [Fact]
    public void JsonRpcNotification_Serializes_WithParams()
    {
        var notification = new JsonRpcNotification
        {
            Method = "notifications/tools/list_changed",
            Params = JsonSerializer.SerializeToElement(new { cursor = "abc" }),
        };

        var json = JsonSerializer.Serialize(notification);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("id", out _).Should().BeFalse();
        doc.RootElement.GetProperty("method").GetString().Should().Be("notifications/tools/list_changed");
        doc.RootElement.GetProperty("params").GetProperty("cursor").GetString().Should().Be("abc");
    }

    [Fact]
    public void JsonRpcNotification_Serializes_WithoutParams()
    {
        var notification = new JsonRpcNotification
        {
            Method = "notifications/initialized",
        };

        var json = JsonSerializer.Serialize(notification);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("params", out _).Should().BeFalse();
    }

    [Fact]
    public void JsonRpcRequest_DefaultVersionIs2_0()
    {
        var request = new JsonRpcRequest { Method = "test" };
        request.Jsonrpc.Should().Be("2.0");
    }

    [Fact]
    public void JsonRpcResponse_DefaultVersionIs2_0()
    {
        var response = new JsonRpcResponse();
        response.Jsonrpc.Should().Be("2.0");
    }

    [Fact]
    public void JsonRpcError_DefaultMessageIsEmpty()
    {
        var error = new JsonRpcError();
        error.Code.Should().Be(0);
        error.Message.Should().BeEmpty();
        error.Data.Should().BeNull();
    }

    [Fact]
    public void JsonRpcResponse_Deserializes_WithStringId()
    {
        var json = """
        {
            "jsonrpc": "2.0",
            "id": "req-abc-123",
            "result": { "ok": true }
        }
        """;

        var response = JsonSerializer.Deserialize<JsonRpcResponse>(json);

        response.Should().NotBeNull();
        response!.Id.Should().NotBeNull();
        response.Result.Should().NotBeNull();
    }

    [Fact]
    public void JsonRpcResponse_Deserializes_ErrorWithStandardCodes()
    {
        // Parse error
        var parseError = JsonSerializer.Deserialize<JsonRpcResponse>("""
        { "jsonrpc": "2.0", "id": null, "error": { "code": -32700, "message": "Parse error" } }
        """);
        parseError!.Error!.Code.Should().Be(-32700);

        // Invalid request
        var invalidReq = JsonSerializer.Deserialize<JsonRpcResponse>("""
        { "jsonrpc": "2.0", "id": null, "error": { "code": -32600, "message": "Invalid Request" } }
        """);
        invalidReq!.Error!.Code.Should().Be(-32600);

        // Method not found
        var methodNotFound = JsonSerializer.Deserialize<JsonRpcResponse>("""
        { "jsonrpc": "2.0", "id": 1, "error": { "code": -32601, "message": "Method not found" } }
        """);
        methodNotFound!.Error!.Code.Should().Be(-32601);
    }
}

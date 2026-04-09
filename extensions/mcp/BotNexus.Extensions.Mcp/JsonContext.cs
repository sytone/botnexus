using System.Text.Json.Serialization;
using BotNexus.Extensions.Mcp.Protocol;

namespace BotNexus.Extensions.Mcp;

/// <summary>
/// Source-generated JSON serialization context for MCP protocol types.
/// </summary>
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcResponse))]
[JsonSerializable(typeof(JsonRpcError))]
[JsonSerializable(typeof(JsonRpcNotification))]
[JsonSerializable(typeof(McpInitializeParams))]
[JsonSerializable(typeof(McpInitializeResult))]
[JsonSerializable(typeof(McpToolsListResult))]
[JsonSerializable(typeof(McpToolCallParams))]
[JsonSerializable(typeof(McpToolCallResult))]
[JsonSerializable(typeof(McpToolDefinition))]
[JsonSerializable(typeof(McpContent))]
[JsonSerializable(typeof(McpServerCapabilities))]
[JsonSerializable(typeof(McpToolCapability))]
[JsonSerializable(typeof(McpServerInfo))]
[JsonSerializable(typeof(McpClientCapabilities))]
[JsonSerializable(typeof(McpClientInfo))]
[JsonSerializable(typeof(McpExtensionConfig))]
[JsonSerializable(typeof(McpServerConfig))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class JsonContext : JsonSerializerContext;

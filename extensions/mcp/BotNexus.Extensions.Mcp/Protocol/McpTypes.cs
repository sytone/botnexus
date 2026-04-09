using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Mcp.Protocol;

/// <summary>
/// An MCP tool definition returned from <c>tools/list</c>.
/// </summary>
public sealed record McpToolDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("inputSchema")]
    public JsonElement InputSchema { get; init; }
}

/// <summary>
/// Result of an MCP <c>tools/call</c> request.
/// </summary>
public sealed record McpToolCallResult
{
    [JsonPropertyName("content")]
    public IReadOnlyList<McpContent> Content { get; init; } = [];

    [JsonPropertyName("isError")]
    public bool IsError { get; init; }
}

/// <summary>
/// Content block within an MCP tool call result.
/// </summary>
public sealed record McpContent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

/// <summary>
/// MCP server capabilities returned from the <c>initialize</c> handshake.
/// </summary>
public sealed record McpServerCapabilities
{
    [JsonPropertyName("tools")]
    public McpToolCapability? Tools { get; init; }
}

/// <summary>
/// Tool capability descriptor.
/// </summary>
public sealed record McpToolCapability
{
    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; init; }
}

/// <summary>
/// Response to the MCP <c>initialize</c> request.
/// </summary>
public sealed record McpInitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; init; } = string.Empty;

    [JsonPropertyName("capabilities")]
    public McpServerCapabilities Capabilities { get; init; } = new();

    [JsonPropertyName("serverInfo")]
    public McpServerInfo? ServerInfo { get; init; }
}

/// <summary>
/// Server identity information.
/// </summary>
public sealed record McpServerInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

/// <summary>
/// Parameters for the MCP <c>initialize</c> request.
/// </summary>
public sealed record McpInitializeParams
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; init; } = "2024-11-05";

    [JsonPropertyName("capabilities")]
    public McpClientCapabilities Capabilities { get; init; } = new();

    [JsonPropertyName("clientInfo")]
    public McpClientInfo ClientInfo { get; init; } = new();
}

/// <summary>
/// Client capabilities sent during initialization.
/// </summary>
public sealed record McpClientCapabilities;

/// <summary>
/// Client identity information sent during initialization.
/// </summary>
public sealed record McpClientInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "BotNexus";

    [JsonPropertyName("version")]
    public string? Version { get; init; } = "1.0.0";
}

/// <summary>
/// Response wrapper for <c>tools/list</c>.
/// </summary>
public sealed record McpToolsListResult
{
    [JsonPropertyName("tools")]
    public IReadOnlyList<McpToolDefinition> Tools { get; init; } = [];
}

/// <summary>
/// Parameters for the MCP <c>tools/call</c> request.
/// </summary>
public sealed record McpToolCallParams
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Arguments { get; init; }
}

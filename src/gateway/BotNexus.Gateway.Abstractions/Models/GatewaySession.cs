namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// A gateway-level session that tracks an interaction between a caller and an agent.
/// Sessions own the conversation history and bridge the Gateway with the underlying
/// <c>AgentCore.Agent</c> execution.
/// </summary>
public sealed class GatewaySession
{
    /// <summary>Unique session identifier.</summary>
    public required string SessionId { get; init; }

    /// <summary>The agent this session is bound to.</summary>
    public required string AgentId { get; set; }

    /// <summary>The channel this session originated from (e.g., "websocket", "telegram").</summary>
    public string? ChannelType { get; set; }

    /// <summary>Caller-specific identifier within the channel (e.g., user ID, chat ID).</summary>
    public string? CallerId { get; set; }

    /// <summary>When the session was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the session was last active.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Conversation history as serializable entries.
    /// This is the Gateway-level view; the AgentCore maintains its own message timeline.
    /// </summary>
    public List<SessionEntry> History { get; init; } = [];

    /// <summary>Session-level metadata for extensibility.</summary>
    public Dictionary<string, object?> Metadata { get; init; } = [];
}

/// <summary>
/// A single entry in the session conversation history.
/// </summary>
public sealed record SessionEntry
{
    /// <summary>Message role: "user", "assistant", "system", or "tool".</summary>
    public required string Role { get; init; }

    /// <summary>Message content.</summary>
    public required string Content { get; init; }

    /// <summary>When this entry was recorded.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Tool name (when Role is "tool").</summary>
    public string? ToolName { get; init; }

    /// <summary>Tool call ID for correlating requests and results.</summary>
    public string? ToolCallId { get; init; }
}

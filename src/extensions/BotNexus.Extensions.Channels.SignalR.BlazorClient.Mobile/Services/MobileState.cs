namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;

/// <summary>Represents a selectable agent.</summary>
public sealed class AgentOption
{
    public required string AgentId { get; init; }
    public required string DisplayName { get; init; }
    public string? Emoji { get; init; }
}

/// <summary>Represents a selectable conversation.</summary>
public sealed class ConversationOption
{
    public required string ConversationId { get; init; }
    public required string Title { get; init; }
}

/// <summary>A single message in the chat history.</summary>
public sealed class ChatMessage
{
    /// <summary>Role: "user", "assistant", "system", "tool", "thinking"</summary>
    public required string Role { get; init; }
    public required string Content { get; init; }
    public string? ToolName { get; init; }
    public bool IsToolCall { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Lightweight observable state container for the mobile chat UI.</summary>
public sealed class MobileState
{
    public List<AgentOption> Agents { get; } = [];
    public List<ConversationOption> Conversations { get; } = [];
    public List<ChatMessage> Messages { get; } = [];

    public string? ActiveAgentId { get; set; }
    public string? ActiveConversationId { get; set; }
    public string? ActiveSessionId { get; set; }

    public bool IsStreaming { get; set; }
    public string StreamBuffer { get; set; } = string.Empty;

    public event Action? OnChanged;
    public void NotifyChanged() => OnChanged?.Invoke();
}

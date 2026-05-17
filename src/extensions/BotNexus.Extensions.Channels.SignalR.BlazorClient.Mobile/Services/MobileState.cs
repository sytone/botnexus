using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;

/// <summary>Represents a selectable agent in the mobile UI.</summary>
public sealed class AgentOption
{
    public required string AgentId { get; init; }
    public required string DisplayName { get; init; }
    public string? Emoji { get; init; }
}

/// <summary>Represents a selectable conversation in the mobile UI.</summary>
public sealed class ConversationOption
{
    public required string ConversationId { get; init; }
    public required string Title { get; init; }
}

/// <summary>Lightweight observable state container for the mobile chat UI.</summary>
public sealed class MobileState
{
    public List<AgentOption> Agents { get; } = [];
    public List<ConversationOption> Conversations { get; } = [];

    /// <summary>Messages use the shared <see cref="ChatMessage"/> from Core.</summary>
    public List<ChatMessage> Messages { get; } = [];

    public string? ActiveAgentId { get; set; }
    public string? ActiveConversationId { get; set; }
    public string? ActiveSessionId { get; set; }

    public bool IsStreaming { get; set; }
    public string StreamBuffer { get; set; } = string.Empty;

    public event Action? OnChanged;
    public void NotifyChanged() => OnChanged?.Invoke();
}


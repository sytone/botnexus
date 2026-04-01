namespace BotNexus.Core.Models;

/// <summary>The role of a participant in a session.</summary>
public enum MessageRole { User, Assistant, System, Tool }

/// <summary>A single entry in the session history.</summary>
public record SessionEntry(
    MessageRole Role,
    string Content,
    DateTimeOffset Timestamp,
    string? ToolName = null,
    string? ToolCallId = null,
    IReadOnlyList<ToolCallRequest>? ToolCalls = null);

/// <summary>Represents a conversation session with an agent.</summary>
public class Session
{
    public string Key { get; init; } = string.Empty;
    public string AgentName { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<SessionEntry> History { get; init; } = [];

    public void AddEntry(SessionEntry entry)
    {
        History.Add(entry);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Clear()
    {
        History.Clear();
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

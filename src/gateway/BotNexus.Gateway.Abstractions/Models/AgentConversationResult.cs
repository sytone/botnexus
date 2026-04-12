using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Result payload for an agent-to-agent conversation.
/// </summary>
public sealed record AgentConversationResult
{
    /// <summary>
    /// Conversation session identifier.
    /// </summary>
    public required SessionId SessionId { get; init; }

    /// <summary>
    /// Final conversation status.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Total transcript entries.
    /// </summary>
    public required int Turns { get; init; }

    /// <summary>
    /// Final target agent response.
    /// </summary>
    public required string FinalResponse { get; init; }

    /// <summary>
    /// Full conversation transcript.
    /// </summary>
    public IReadOnlyList<AgentConversationTranscriptEntry> Transcript { get; init; } = [];
}

/// <summary>
/// A single transcript entry from an agent-to-agent conversation.
/// </summary>
/// <param name="Role">Message role.</param>
/// <param name="Content">Message content.</param>
public sealed record AgentConversationTranscriptEntry(string Role, string Content);

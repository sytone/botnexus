using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Result payload for an agent-to-agent conversation.
/// </summary>
public sealed record AgentExchangeResult
{
    /// <summary>Conversation session identifier.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Final conversation status.</summary>
    public required string Status { get; init; }

    /// <summary>Total transcript entries.</summary>
    public required int Turns { get; init; }

    /// <summary>Final target agent response.</summary>
    public required string FinalResponse { get; init; }

    /// <summary>Full conversation transcript.</summary>
    public IReadOnlyList<AgentExchangeTranscriptEntry> Transcript { get; init; } = [];

    /// <summary>
    /// Why the conversation loop ended.
    /// <list type="bullet">
    ///   <item><c>objectiveMet</c> — target agent signalled "OBJECTIVE MET"</item>
    ///   <item><c>maxTurnsReached</c> — loop exhausted all turns without a completion signal</item>
    ///   <item><c>error</c> — an exception was thrown during the exchange</item>
    /// </list>
    /// </summary>
    public string? CompletionReason { get; init; }
}

/// <summary>A single transcript entry from an agent-to-agent conversation.</summary>
/// <param name="Role">Message role.</param>
/// <param name="Content">Message content.</param>
public sealed record AgentExchangeTranscriptEntry(string Role, string Content);

using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Result payload for an agent-to-agent conversation.
/// </summary>
public sealed record AgentExchangeResult
{
    /// <summary>Conversation session identifier.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>
    /// Identifier of the <see cref="Conversation"/> the exchange created (one new conversation
    /// per <c>ConverseAsync</c> call — agent-to-agent exchanges are one-shot bounded loops).
    /// Callers can use this to retrieve the persisted transcript via
    /// <c>IConversationStore.GetAsync</c> or <c>ISessionStore.ListByConversationAsync</c>.
    /// </summary>
    public required ConversationId ConversationId { get; init; }

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
    ///   <item><c>exchangeFinished</c> — the target agent invoked the <c>finish_agent_exchange</c>
    ///   tool successfully (Phase 8 / F-11 — replaces the deprecated <c>objectiveMet</c> substring
    ///   heuristic from issue #379).</item>
    ///   <item><c>maxTurnsReached</c> — loop exhausted all turns without a completion signal</item>
    ///   <item><c>error</c> — an exception was thrown during the exchange</item>
    /// </list>
    /// </summary>
    public string? CompletionReason { get; init; }

    /// <summary>
    /// Caller-supplied <c>reason</c> from the <c>finish_agent_exchange</c> tool call when
    /// <see cref="CompletionReason"/> is <c>exchangeFinished</c>; <c>null</c> otherwise.
    /// </summary>
    public string? FinishReason { get; init; }

    /// <summary>
    /// Caller-supplied <c>summary</c> from the <c>finish_agent_exchange</c> tool call when
    /// <see cref="CompletionReason"/> is <c>exchangeFinished</c>; <c>null</c> otherwise.
    /// </summary>
    public string? FinishSummary { get; init; }
}

/// <summary>A single transcript entry from an agent-to-agent conversation.</summary>
/// <param name="Role">Message role.</param>
/// <param name="Content">Message content.</param>
public sealed record AgentExchangeTranscriptEntry(string Role, string Content);

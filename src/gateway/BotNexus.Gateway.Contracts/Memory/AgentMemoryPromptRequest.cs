namespace BotNexus.Gateway.Contracts.Memory;

/// <summary>
/// Request parameters for retrieving memory context to include in an agent's prompt.
/// </summary>
/// <param name="AgentId">The agent whose memory to query.</param>
/// <param name="SessionId">The current session identifier for relevance scoping.</param>
/// <param name="ConversationId">Optional conversation identifier for topic filtering.</param>
/// <param name="MaxTokenBudget">
/// Maximum approximate token budget for the returned context, estimated at four characters per
/// token. Providers <b>must</b> respect this limit when assembling content and <b>must</b>
/// disclose in the returned content that trimming occurred - see
/// <c>BotNexus.Memory.MemoryPromptBudget</c> for the trimming order and disclosure format (#2871).
/// A value of zero or below means explicitly unbounded, so "no cap" is a deliberate caller choice
/// that is distinguishable from an ignored parameter.
/// </param>
public sealed record AgentMemoryPromptRequest(
    string AgentId,
    string? SessionId = null,
    string? ConversationId = null,
    int MaxTokenBudget = 4000);

/// <summary>
/// Memory context assembled by the provider for prompt inclusion.
/// Contains structured sections that the prompt pipeline can arrange.
/// </summary>
/// <param name="LongTermMemory">
/// Consolidated long-term memory content (e.g. MEMORY.md equivalent).
/// </param>
/// <param name="DailyNotes">
/// Recent daily notes relevant to the current context, ordered newest first.
/// </param>
/// <param name="ApproximateTokenCount">
/// Estimated token count of the assembled context for budget tracking.
/// </param>
public sealed record AgentMemoryContext(
    string? LongTermMemory,
    IReadOnlyList<AgentMemoryDailyNote> DailyNotes,
    int ApproximateTokenCount)
{
    /// <summary>
    /// An empty context with no memory content.
    /// </summary>
    public static AgentMemoryContext Empty { get; } = new(null, Array.Empty<AgentMemoryDailyNote>(), 0);
}

/// <summary>
/// A single daily note entry within the memory context.
/// </summary>
/// <param name="Date">The date this note covers.</param>
/// <param name="Content">The markdown content of the daily note.</param>
/// <param name="Provenance">
/// Where the note's content came from (issue #2480). Defaults to <c>unknown</c> - the fail-safe
/// value - so a provider that does not record provenance cannot have its notes rendered as
/// first-party agent knowledge.
/// </param>
public sealed record AgentMemoryDailyNote(
    DateOnly Date,
    string Content,
    string Provenance = "unknown");

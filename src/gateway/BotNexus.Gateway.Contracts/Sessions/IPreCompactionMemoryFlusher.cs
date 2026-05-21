using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Pre-compaction memory flush service.
/// Fires a synthetic memory-trigger agent turn before compaction so the agent
/// can write important context to disk (e.g. memory/YYYY-MM-DD.md) before the
/// conversation history is summarised and truncated.
/// </summary>
public interface IPreCompactionMemoryFlusher
{
    /// <summary>
    /// Returns true when a memory flush should run before the next compaction cycle.
    /// </summary>
    bool ShouldFlush(Session session, CompactionOptions options);

    /// <summary>
    /// Fires a memory-trigger agent turn and awaits completion (with timeout).
    /// Non-fatal: exceptions are caught and logged — compaction always proceeds.
    /// </summary>
    Task FlushAsync(AgentId agentId, Session session, CompactionOptions options, CancellationToken ct = default);
}

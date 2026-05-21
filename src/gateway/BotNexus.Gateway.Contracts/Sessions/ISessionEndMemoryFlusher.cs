using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Session-end memory flush service.
/// Fires a synthetic memory-trigger agent turn when a session is explicitly
/// reset or closed, giving the agent an opportunity to persist important context
/// before the session history is archived.
/// </summary>
public interface ISessionEndMemoryFlusher
{
    /// <summary>
    /// Returns true when a session-end memory flush should run.
    /// </summary>
    bool ShouldFlush(Session session, CompactionOptions options);

    /// <summary>
    /// Fires a memory-trigger agent turn and awaits completion (with timeout).
    /// Non-fatal: exceptions are caught and logged — session reset always proceeds.
    /// </summary>
    Task FlushAsync(AgentId agentId, Session session, CompactionOptions options, CancellationToken ct = default);
}

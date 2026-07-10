using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Fires a synthetic memory-flush agent turn immediately before session compaction by
/// steering the existing live agent handle. Gives the agent an opportunity to write
/// important context to disk before history is summarised and truncated.
/// </summary>
/// <remarks>
/// The flush is performed via <see cref="IAgentSupervisor"/> steering into the live
/// handle — no new session or conversation is created. If no live handle exists the
/// flush is skipped (the agent is not running so there is nothing to save).
/// </remarks>
public sealed class PreCompactionMemoryFlusher : IPreCompactionMemoryFlusher
{
    private readonly IAgentSupervisor _supervisor;
    private readonly ILogger<PreCompactionMemoryFlusher> _logger;

    public PreCompactionMemoryFlusher(
        IAgentSupervisor supervisor,
        ILogger<PreCompactionMemoryFlusher> logger)
    {
        _supervisor = supervisor;
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool ShouldFlush(Session session, CompactionOptions options)
    {
        if (!options.MemoryFlush.Enabled)
            return false;

        // Never flush for non-interactive sessions (heartbeat, cron, sub-agent)
        if (!session.IsInteractive)
            return false;

        // One flush per compaction cycle: track the compaction-cycle count in metadata.
        // We infer the current cycle count from the number of compaction summary entries.
        var currentCycle = session.History.Count(e => e.IsCompactionSummary);
        var lastFlushCycle = GetLastFlushCycle(session);

        return lastFlushCycle < currentCycle + 1; // flush has not run for the upcoming cycle
    }

    /// <inheritdoc/>
    public async Task FlushAsync(AgentId agentId, Session session, CompactionOptions options, CancellationToken ct = default)
    {
        // Use the live agent handle — no new session/conversation is created.
        var handle = _supervisor.GetHandle(agentId, session.SessionId);
        if (handle is null)
        {
            _logger.LogDebug(
                "Pre-compaction memory flush skipped for session {SessionId}: no live agent handle found.",
                session.SessionId);
            return;
        }

        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.MemoryFlush.TimeoutSeconds));

        try
        {
            _logger.LogInformation(
                "Pre-compaction memory flush starting for session {SessionId}, agent {AgentId}.",
                session.SessionId, agentId);

            await handle.SteerDeferrableAsync(options.MemoryFlush.PromptText, timeoutCts.Token).ConfigureAwait(false);

            // Record that a flush has run for this compaction cycle.
            var currentCycle = session.History.Count(e => e.IsCompactionSummary) + 1;
            session.Metadata[MemoryFlushOptions.MetadataKey] = currentCycle;

            _logger.LogInformation(
                "Pre-compaction memory flush completed for session {SessionId}.",
                session.SessionId);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Pre-compaction memory flush timed out after {Seconds}s for session {SessionId}. Compaction will proceed.",
                options.MemoryFlush.TimeoutSeconds, session.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Pre-compaction memory flush failed for session {SessionId}. Compaction will proceed.",
                session.SessionId);
        }
        finally
        {
            timeoutCts.Dispose();
        }
    }

    private static int GetLastFlushCycle(Session session)
    {
        if (session.Metadata.TryGetValue(MemoryFlushOptions.MetadataKey, out var raw))
        {
            return raw switch
            {
                int i => i,
                long l => (int)l,
                System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number
                    => je.GetInt32(),
                _ => 0
            };
        }
        return 0;
    }
}

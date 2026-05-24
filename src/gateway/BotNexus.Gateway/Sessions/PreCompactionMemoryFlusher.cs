using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Fires a synthetic memory-trigger agent turn immediately before session compaction.
/// Gives the agent an opportunity to write important context to disk before history is
/// summarised and truncated.
/// </summary>
public sealed class PreCompactionMemoryFlusher : IPreCompactionMemoryFlusher
{
    private readonly IEnumerable<IInternalTrigger> _triggers;
    private readonly ILogger<PreCompactionMemoryFlusher> _logger;

    public PreCompactionMemoryFlusher(
        IEnumerable<IInternalTrigger> triggers,
        ILogger<PreCompactionMemoryFlusher> logger)
    {
        _triggers = triggers;
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
        var trigger = ResolveTrigger();
        if (trigger is null)
        {
            _logger.LogWarning(
                "No suitable internal trigger found for memory flush on session {SessionId}. Skipping flush.",
                session.SessionId);
            return;
        }

        var timeoutCt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCt.CancelAfter(TimeSpan.FromSeconds(options.MemoryFlush.TimeoutSeconds));

        try
        {
            _logger.LogInformation(
                "Pre-compaction memory flush starting for session {SessionId}, agent {AgentId}.",
                session.SessionId, agentId);

            await trigger.CreateSessionAsync(
                agentId,
                options.MemoryFlush.PromptText,
                timeoutCt.Token,
                new InternalTriggerRequest
                {
                    ConversationId = session.ConversationId
                }).ConfigureAwait(false);

            // Record that a flush has run for this compaction cycle.
            var currentCycle = session.History.Count(e => e.IsCompactionSummary) + 1;
            session.Metadata[MemoryFlushOptions.MetadataKey] = currentCycle;

            _logger.LogInformation(
                "Pre-compaction memory flush completed for session {SessionId}.",
                session.SessionId);
        }
        catch (OperationCanceledException) when (timeoutCt.IsCancellationRequested && !ct.IsCancellationRequested)
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
            timeoutCt.Dispose();
        }
    }

    private IInternalTrigger? ResolveTrigger()
    {
        var all = _triggers.ToList();
        return all.FirstOrDefault(t => t.Type.Equals(TriggerType.Memory))
            ?? all.FirstOrDefault(t => t.Type.Equals(TriggerType.Cron));
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

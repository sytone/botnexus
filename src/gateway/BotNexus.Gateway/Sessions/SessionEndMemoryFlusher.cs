using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Fires a synthetic memory-trigger agent turn immediately before a session is archived
/// (i.e. on /reset or explicit session close).
/// Gives the agent an opportunity to persist important context before the session history
/// is discarded.
/// </summary>
public sealed class SessionEndMemoryFlusher : ISessionEndMemoryFlusher
{
    private readonly IEnumerable<IInternalTrigger> _triggers;
    private readonly ILogger<SessionEndMemoryFlusher> _logger;

    public SessionEndMemoryFlusher(
        IEnumerable<IInternalTrigger> triggers,
        ILogger<SessionEndMemoryFlusher> logger)
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

        // Skip sessions with no user turns — nothing worth flushing
        return session.History.Any(e => e.Role == MessageRole.User);
    }

    /// <inheritdoc/>
    public async Task FlushAsync(AgentId agentId, Session session, CompactionOptions options, CancellationToken ct = default)
    {
        var trigger = ResolveTrigger();
        if (trigger is null)
        {
            _logger.LogWarning(
                "No suitable internal trigger found for session-end memory flush on session {SessionId}. Skipping flush.",
                session.SessionId);
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.MemoryFlush.TimeoutSeconds));

        try
        {
            _logger.LogInformation(
                "Session-end memory flush starting for session {SessionId}, agent {AgentId}.",
                session.SessionId, agentId);

            await trigger.CreateSessionAsync(
                agentId,
                options.MemoryFlush.SessionEndPromptText,
                timeoutCts.Token,
                new InternalTriggerRequest
                {
                    ConversationId = session.ConversationId?.Value
                }).ConfigureAwait(false);

            _logger.LogInformation(
                "Session-end memory flush completed for session {SessionId}.",
                session.SessionId);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Session-end memory flush timed out after {Seconds}s for session {SessionId}. Session will proceed with reset.",
                options.MemoryFlush.TimeoutSeconds, session.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Session-end memory flush failed for session {SessionId}. Session will proceed with reset.",
                session.SessionId);
        }
    }

    private IInternalTrigger? ResolveTrigger()
    {
        var all = _triggers.ToList();
        return all.FirstOrDefault(t => t.Type.Equals(TriggerType.Memory))
            ?? all.FirstOrDefault(t => t.Type.Equals(TriggerType.Cron));
    }
}

using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Domain.Primitives;
using BotNexus.Memory.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Memory;

public sealed class MemoryIndexer(
    IMemoryStoreFactory storeFactory,
    ISessionLifecycleEvents lifecycleEvents,
    ILogger<MemoryIndexer> logger) : IHostedService
{
    private readonly IMemoryStoreFactory _storeFactory = storeFactory;
    private readonly ISessionLifecycleEvents _lifecycleEvents = lifecycleEvents;
    private readonly ILogger<MemoryIndexer> _logger = logger;
    private readonly CancellationTokenSource _stoppingCts = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifecycleEvents.SessionChanged += OnSessionChangedAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _lifecycleEvents.SessionChanged -= OnSessionChangedAsync;
        _stoppingCts.Cancel();
        return Task.CompletedTask;
    }

    private Task OnSessionChangedAsync(SessionLifecycleEvent lifecycleEvent, CancellationToken cancellationToken)
    {
        if (lifecycleEvent.Session is null ||
            (lifecycleEvent.Type != SessionLifecycleEventType.Closed &&
             lifecycleEvent.Type != SessionLifecycleEventType.Expired))
        {
            return Task.CompletedTask;
        }

        _ = Task.Run(async () =>
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stoppingCts.Token);
            try
            {
                await IndexSessionAsync(lifecycleEvent, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Memory indexing failed for session '{SessionId}'.", lifecycleEvent.SessionId);
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task IndexSessionAsync(SessionLifecycleEvent lifecycleEvent, CancellationToken cancellationToken)
    {
        var session = lifecycleEvent.Session!;
        var store = _storeFactory.Create(lifecycleEvent.AgentId);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await IndexSessionCoreAsync(session, lifecycleEvent.AgentId, lifecycleEvent.SessionId, store, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Core indexing logic that extracts user/assistant turn pairs from a session and inserts
    /// any turns not already indexed into the memory store.
    /// </summary>
    /// <returns>The number of new turns indexed.</returns>
    internal static async Task<int> IndexSessionCoreAsync(
        GatewaySession session,
        AgentId agentId,
        SessionId sessionId,
        IMemoryStore store,
        CancellationToken ct)
    {
        var existing = await store.GetBySessionAsync(sessionId, int.MaxValue, ct).ConfigureAwait(false);
        var indexedTurns = existing
            .Where(entry => entry.TurnIndex.HasValue)
            .Select(entry => entry.TurnIndex!.Value)
            .ToHashSet();

        var history = session.GetHistorySnapshot();
        SessionEntry? pendingUser = null;
        int? pendingTurnIndex = null;
        var turnsIndexed = 0;

        for (var i = 0; i < history.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = history[i];
            if (entry.Role.Equals(MessageRole.Tool))
                continue;

            if (entry.Role.Equals(MessageRole.User))
            {
                pendingUser = entry;
                pendingTurnIndex = i;
                continue;
            }

            if (pendingUser is null || pendingTurnIndex is null || !entry.Role.Equals(MessageRole.Assistant))
                continue;

            if (!indexedTurns.Contains(pendingTurnIndex.Value))
            {
                var memory = new MemoryEntry
                {
                    Id = string.Empty,
                    AgentId = agentId,
                    SessionId = sessionId,
                    TurnIndex = pendingTurnIndex,
                    SourceType = "conversation",
                    Content = $"User: {pendingUser.Content}\nAssistant: {entry.Content}",
                    MetadataJson = null,
                    Embedding = null,
                    CreatedAt = entry.Timestamp,
                    UpdatedAt = null,
                    ExpiresAt = null,
                    IsArchived = false
                };

                await store.InsertAsync(memory, ct).ConfigureAwait(false);
                indexedTurns.Add(pendingTurnIndex.Value);
                turnsIndexed++;
            }

            pendingUser = null;
            pendingTurnIndex = null;
        }

        return turnsIndexed;
    }

    /// <summary>
    /// Backfills memory entries from all existing sessions in the session store.
    /// Can be called standalone without the hosted service running.
    /// </summary>
    /// <param name="sessionStore">The session store to read sessions from.</param>
    /// <param name="storeFactory">Factory to create per-agent memory stores.</param>
    /// <param name="logger">Logger for progress reporting.</param>
    /// <param name="agentFilter">Optional agent ID to limit backfill to a single agent.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A summary of sessions processed and turns indexed.</returns>
    public static async Task<BackfillResult> BackfillAsync(
        ISessionStore sessionStore,
        IMemoryStoreFactory storeFactory,
        ILogger logger,
        AgentId? agentFilter = null,
        CancellationToken ct = default)
    {
        var sessions = await sessionStore.ListAsync(agentFilter, ct).ConfigureAwait(false);
        logger.LogInformation("Memory backfill starting — {SessionCount} session(s) to process.", sessions.Count);

        var totalTurns = 0;
        var sessionsProcessed = 0;

        foreach (var session in sessions)
        {
            ct.ThrowIfCancellationRequested();

            var agentId = session.AgentId;
            var sessionId = session.SessionId;

            try
            {
                var store = storeFactory.Create(agentId);
                await store.InitializeAsync(ct).ConfigureAwait(false);

                var turnsIndexed = await IndexSessionCoreAsync(session, agentId, sessionId, store, ct).ConfigureAwait(false);
                sessionsProcessed++;
                totalTurns += turnsIndexed;

                if (turnsIndexed > 0)
                {
                    logger.LogInformation(
                        "Indexed {TurnsIndexed} turn(s) from session '{SessionId}' (agent: {AgentId}).",
                        turnsIndexed, sessionId, agentId);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to backfill session '{SessionId}' (agent: {AgentId}).", sessionId, agentId);
            }
        }

        logger.LogInformation(
            "Memory backfill complete — {SessionsProcessed} session(s) processed, {TotalTurns} turn(s) indexed.",
            sessionsProcessed, totalTurns);

        return new BackfillResult(sessionsProcessed, totalTurns);
    }
}

/// <summary>
/// Summary of a memory backfill operation.
/// </summary>
public readonly record struct BackfillResult(int SessionsProcessed, int TurnsIndexed);

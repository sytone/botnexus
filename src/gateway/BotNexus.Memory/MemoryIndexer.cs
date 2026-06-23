using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Domain.Primitives;
using BotNexus.Memory.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Memory;

public sealed class MemoryIndexer(
    IAgentMemoryFactory agentMemoryFactory,
    IMemoryStoreFactory storeFactory,
    ISessionLifecycleEvents lifecycleEvents,
    ILogger<MemoryIndexer> logger) : IHostedService
{
    private readonly IAgentMemoryFactory _agentMemoryFactory = agentMemoryFactory;
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
        var agentId = lifecycleEvent.AgentId;
        var sessionId = lifecycleEvent.SessionId;

        var sessionEvent = BuildSessionEvent(session, agentId, sessionId);

        try
        {
            var agentMemory = _agentMemoryFactory.Create(agentId);
            await agentMemory.OnSessionCompleteAsync(sessionEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            // Provider not registered — fall back to direct store indexing
            var store = _storeFactory.Create(agentId);
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await IndexSessionCoreAsync(session, AgentId.From(agentId), SessionId.From(sessionId), store, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds an <see cref="AgentMemorySessionEvent"/> from a gateway session, including history turns.
    /// </summary>
    internal static AgentMemorySessionEvent BuildSessionEvent(GatewaySession session, string agentId, string sessionId)
    {
        var history = session.GetHistorySnapshot();
        var turns = new List<AgentMemorySessionTurn>(history.Count);
        for (var i = 0; i < history.Count; i++)
        {
            var entry = history[i];
            turns.Add(new AgentMemorySessionTurn(i, entry.Role.Value, entry.Content, entry.Timestamp));
        }

        return new AgentMemorySessionEvent(
            AgentId: agentId,
            SessionId: sessionId,
            ConversationId: null,
            EndedAt: DateTimeOffset.UtcNow,
            TurnCount: history.Count,
            History: turns);
    }

    /// <summary>
    /// Core indexing logic that extracts user/assistant turn pairs from a session and inserts
    /// any turns not already indexed into the memory store.
    /// Preserved as internal fallback for when IAgentMemory is unavailable.
    /// </summary>
    /// <returns>The number of new turns indexed.</returns>
    internal static async Task<int> IndexSessionCoreAsync(
        GatewaySession session,
        AgentId agentId,
        SessionId sessionId,
        IMemoryStore store,
        CancellationToken ct)
    {
        var existing = await store.GetBySessionAsync(sessionId.Value, int.MaxValue, ct).ConfigureAwait(false);
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
                    AgentId = agentId.Value,
                    SessionId = sessionId.Value,
                    TurnIndex = pendingTurnIndex,
                    SourceType = "conversation",
                    // Strip LLM control / role-injection markup before persisting raw transcript
                    // text to the searchable store — defends against memory-poisoning (#1560).
                    Content = $"User: {MemoryContentSanitizer.Sanitize(pendingUser.Content)}\nAssistant: {MemoryContentSanitizer.Sanitize(entry.Content)}",
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
    /// Routes through IAgentMemory when available, falling back to direct store access.
    /// </summary>
    public static async Task<BackfillResult> BackfillAsync(
        ISessionStore sessionStore,
        IAgentMemoryFactory agentMemoryFactory,
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
                var sessionEvent = BuildSessionEvent(session, agentId.Value, sessionId.Value);

                try
                {
                    var agentMemory = agentMemoryFactory.Create(agentId.Value);
                    await agentMemory.OnSessionCompleteAsync(sessionEvent, ct).ConfigureAwait(false);
                }
                catch (NotSupportedException)
                {
                    var store = storeFactory.Create(agentId.Value);
                    await store.InitializeAsync(ct).ConfigureAwait(false);
                    await IndexSessionCoreAsync(session, agentId, sessionId, store, ct).ConfigureAwait(false);
                }

                sessionsProcessed++;
                totalTurns += sessionEvent.TurnCount;
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

    /// <summary>
    /// Legacy overload for backward compatibility.
    /// </summary>
    public static Task<BackfillResult> BackfillAsync(
        ISessionStore sessionStore,
        IMemoryStoreFactory storeFactory,
        ILogger logger,
        AgentId? agentFilter = null,
        CancellationToken ct = default)
    {
        // Create a throwing factory to force fallback path
        return BackfillAsync(sessionStore, new ThrowingAgentMemoryFactory(), storeFactory, logger, agentFilter, ct);
    }

    private sealed class ThrowingAgentMemoryFactory : IAgentMemoryFactory
    {
        public IAgentMemory Create(string agentId, string? providerName = null)
            => throw new NotSupportedException();

        public IReadOnlyList<string> GetRegisteredProviders() => [];
    }
}

/// <summary>
/// Summary of a memory backfill operation.
/// </summary>
public readonly record struct BackfillResult(int SessionsProcessed, int TurnsIndexed);

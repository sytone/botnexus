using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
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

        var existing = await store.GetBySessionAsync(lifecycleEvent.SessionId, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var indexedTurns = existing
            .Where(entry => entry.TurnIndex.HasValue)
            .Select(entry => entry.TurnIndex!.Value)
            .ToHashSet();

        var history = session.GetHistorySnapshot();
        SessionEntry? pendingUser = null;
        int? pendingTurnIndex = null;
        for (var i = 0; i < history.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = history[i];
            if (string.Equals(entry.Role, "tool", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(entry.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                pendingUser = entry;
                pendingTurnIndex = i;
                continue;
            }

            if (pendingUser is null || pendingTurnIndex is null || !string.Equals(entry.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!indexedTurns.Contains(pendingTurnIndex.Value))
            {
                var memory = new MemoryEntry
                {
                    Id = string.Empty,
                    AgentId = lifecycleEvent.AgentId,
                    SessionId = lifecycleEvent.SessionId,
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

                await store.InsertAsync(memory, cancellationToken).ConfigureAwait(false);
                indexedTurns.Add(pendingTurnIndex.Value);
            }

            pendingUser = null;
            pendingTurnIndex = null;
        }
    }
}

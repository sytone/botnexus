using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Memory;
using BotNexus.Memory.Embeddings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Providers;

/// <summary>
/// Advances durable per-store re-embedding jobs in small sequential batches.
/// </summary>
public sealed class MemoryReembeddingWorker : BackgroundService
{
    private readonly IAgentRegistry _agentRegistry;
    private readonly IMemoryStoreFactory _storeFactory;
    private readonly IMemoryEmbeddingService _embeddingService;
    private readonly MemoryReembeddingOptions _options;
    private readonly ILogger<MemoryReembeddingWorker> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private int _nextAgentIndex;

    /// <summary>
    /// Creates a worker over the same embedding service and memory-store factory used by interactive memory operations.
    /// </summary>
    public MemoryReembeddingWorker(
        IAgentRegistry agentRegistry,
        IMemoryStoreFactory storeFactory,
        IMemoryEmbeddingService embeddingService,
        IOptions<MemoryReembeddingOptions> options,
        ILogger<MemoryReembeddingWorker> logger)
        : this(agentRegistry, storeFactory, embeddingService, options, logger, Task.Delay)
    {
    }

    internal MemoryReembeddingWorker(
        IAgentRegistry agentRegistry,
        IMemoryStoreFactory storeFactory,
        IMemoryEmbeddingService embeddingService,
        IOptions<MemoryReembeddingOptions> options,
        ILogger<MemoryReembeddingWorker> logger,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _agentRegistry = agentRegistry;
        _storeFactory = storeFactory;
        _embeddingService = embeddingService;
        _options = options.Value;
        _logger = logger;
        _delay = delay;
    }

    /// <summary>
    /// Executes one bounded deterministic pass; exposed so the scheduling policy can be tested without starting a host.
    /// </summary>
    public async Task ProcessPassAsync(CancellationToken ct)
    {
        var target = _embeddingService.ActiveIdentity;
        if (target is null)
            return;

        var registeredAgents = _agentRegistry.GetAll()
            .OrderBy(agent => agent.AgentId.Value, StringComparer.Ordinal)
            .ToArray();
        if (registeredAgents.Length == 0)
            return;

        var batchSize = Math.Min(_options.AgentBatchSize, registeredAgents.Length);
        var start = _nextAgentIndex % registeredAgents.Length;
        _nextAgentIndex = (start + batchSize) % registeredAgents.Length;
        var agents = Enumerable.Range(0, batchSize)
            .Select(offset => registeredAgents[(start + offset) % registeredAgents.Length]);

        foreach (var agent in agents)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!_storeFactory.StoreLocationExists(agent.AgentId))
                    continue;

                var store = _storeFactory.Create(agent.AgentId);
                var job = await store.EnsureReembeddingJobAsync(target, ct).ConfigureAwait(false);
                var items = await store.ClaimReembeddingBatchAsync(job.JobId, _options.ItemBatchSize, ct).ConfigureAwait(false);
                foreach (var item in items)
                {
                    await ProcessItemAsync(store, job, item, ct).ConfigureAwait(false);
                    await _delay(_options.ItemYieldDelay, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Re-embedding pass failed for agent '{AgentId}'; continuing with the next agent.", agent.AgentId);
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_embeddingService.ActiveIdentity is null)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessPassAsync(stoppingToken).ConfigureAwait(false);
            await _delay(_options.PassDelay, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessItemAsync(IMemoryStore store, ReembeddingJob job, ReembeddingItem item, CancellationToken ct)
    {
        try
        {
            var generated = await _embeddingService.TryGenerateAsync(item.Content, ct).ConfigureAwait(false);
            if (generated is null)
            {
                await store.FailReembeddingItemAsync(
                    job.JobId,
                    item.MemoryId,
                    item.Revision,
                    item.ClaimToken,
                    $"Embedding generation returned no vector for target identity '{job.TargetIdentity}'.",
                    ct).ConfigureAwait(false);
                return;
            }

            if (!job.TargetIdentity.Matches(generated.Value.Identity))
            {
                await store.FailReembeddingItemAsync(
                    job.JobId,
                    item.MemoryId,
                    item.Revision,
                    item.ClaimToken,
                    $"Embedding identity mismatch: target '{job.TargetIdentity}', generated '{generated.Value.Identity}'.",
                    ct).ConfigureAwait(false);
                return;
            }

            var blob = EmbeddingBlob.Encode(generated.Value.Identity, generated.Value.Vector);
            await store.CompleteReembeddingItemAsync(
                job.JobId, item.MemoryId, item.Revision, item.ClaimToken, blob, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await store.FailReembeddingItemAsync(
                job.JobId,
                item.MemoryId,
                item.Revision,
                item.ClaimToken,
                $"Embedding generation failed: {ex.GetType().Name}: {ex.Message}",
                ct).ConfigureAwait(false);
        }
    }
}

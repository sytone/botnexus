using System.Text;
using BotNexus.Gateway.Abstractions.Concurrency;
using System.Threading.Channels;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Evaluations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Evaluations;

/// <summary>Bounds for the best-effort in-memory post-run evaluation coordinator.</summary>
public sealed class PostRunEvaluationOptions
{
    public int Capacity { get; init; } = 128;
    public TimeSpan PerEvaluatorTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxResultDetailBytes { get; init; } = 4 * 1024;
    public int DeduplicationCapacity { get; init; } = 4096;
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Bounded, non-blocking, loss-on-restart coordinator. Evaluators execute sequentially in DI
/// registration order for each snapshot; timeout or failure in one never prevents the next.
/// </summary>
public sealed class PostRunEvaluationCoordinator : BackgroundService, IPostRunEvaluationCoordinator
{
    private readonly IReadOnlyList<IPostRunEvaluator> _evaluators;
    private readonly PostRunEvaluationOptions _options;
    private readonly ILogger<PostRunEvaluationCoordinator> _logger;
    private readonly Channel<RunOutcomeSnapshot> _queue;
    private readonly BoundedLruCache<RunId, byte> _admitted;
    private readonly HashSet<(PostRunEvaluatorId, PostRunEvaluatorVersion)> _timedOutEvaluators = [];
    private readonly Lock _admissionGate = new();

    public PostRunEvaluationCoordinator(
        IEnumerable<IPostRunEvaluator> evaluators,
        PostRunEvaluationOptions options,
        ILogger<PostRunEvaluationCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(evaluators);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        if (options.Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Capacity must be positive.");
        if (options.PerEvaluatorTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Per-evaluator timeout must be positive.");
        if (options.MaxResultDetailBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Result detail bound cannot be negative.");
        if (options.DeduplicationCapacity < options.Capacity + 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Deduplication capacity must retain the full queue plus the in-flight run.");
        }

        _evaluators = evaluators.ToArray();
        EnsureUniqueDescriptors(_evaluators);
        _options = options;
        _logger = logger;
        _admitted = new BoundedLruCache<RunId, byte>(options.DeduplicationCapacity);
        _queue = Channel.CreateBounded<RunOutcomeSnapshot>(new BoundedChannelOptions(options.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public PostRunEvaluationAdmission Enqueue(RunOutcomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_options.Enabled || _evaluators.Count == 0)
            return PostRunEvaluationAdmission.Disabled;
        lock (_admissionGate)
        {
            if (_admitted.TryGet(snapshot.RunId, out _))
                return PostRunEvaluationAdmission.Duplicate;
            if (!_queue.Writer.TryWrite(snapshot))
                return PostRunEvaluationAdmission.Saturated;

            _admitted.Set(snapshot.RunId, 0);
            return PostRunEvaluationAdmission.Accepted;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var snapshot in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            foreach (var evaluator in _evaluators)
            {
                await EvaluateOneAsync(evaluator, snapshot, stoppingToken).ConfigureAwait(false);
                if (stoppingToken.IsCancellationRequested)
                    break;
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private async Task EvaluateOneAsync(
        IPostRunEvaluator evaluator,
        RunOutcomeSnapshot snapshot,
        CancellationToken stoppingToken)
    {
        var evaluatorIdentity = (evaluator.Descriptor.Id, evaluator.Descriptor.Version);
        if (_timedOutEvaluators.Contains(evaluatorIdentity))
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(_options.PerEvaluatorTimeout);
        Task<PostRunEvaluationResult>? evaluationTask = null;
        try
        {
            evaluationTask = evaluator.EvaluateAsync(snapshot, timeout.Token).AsTask();
            var result = await evaluationTask.WaitAsync(timeout.Token).ConfigureAwait(false);
            var detail = Bound(result.Detail, _options.MaxResultDetailBytes);
            _logger.LogDebug(
                "Post-run evaluator {EvaluatorId} v{EvaluatorVersion} completed run {RunId} with {Status}: {Detail}",
                evaluator.Descriptor.Id.Value,
                evaluator.Descriptor.Version,
                snapshot.RunId.Value,
                result.Status,
                detail);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            _timedOutEvaluators.Add(evaluatorIdentity);
            ObserveLateCompletion(evaluationTask);
            _logger.LogWarning(
                "Post-run evaluator {EvaluatorId} v{EvaluatorVersion} timed out for run {RunId}; " +
                "the evaluator is disabled until gateway restart",
                evaluator.Descriptor.Id.Value,
                evaluator.Descriptor.Version,
                snapshot.RunId.Value);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            ObserveLateCompletion(evaluationTask);
            _logger.LogDebug(
                "Post-run evaluator {EvaluatorId} cancelled during host shutdown for run {RunId}",
                evaluator.Descriptor.Id.Value,
                snapshot.RunId.Value);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Post-run evaluator {EvaluatorId} v{EvaluatorVersion} failed for run {RunId}",
                evaluator.Descriptor.Id.Value,
                evaluator.Descriptor.Version,
                snapshot.RunId.Value);
        }
    }

    private static void ObserveLateCompletion(Task<PostRunEvaluationResult>? evaluationTask)
    {
        if (evaluationTask is null || evaluationTask.IsCompletedSuccessfully || evaluationTask.IsCanceled)
            return;

        _ = evaluationTask.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void EnsureUniqueDescriptors(IEnumerable<IPostRunEvaluator> evaluators)
    {
        var seen = new HashSet<(PostRunEvaluatorId, PostRunEvaluatorVersion)>();
        foreach (var evaluator in evaluators)
        {
            if (!seen.Add((evaluator.Descriptor.Id, evaluator.Descriptor.Version)))
            {
                throw new InvalidOperationException(
                    $"Duplicate post-run evaluator registration: {evaluator.Descriptor.Id} v{evaluator.Descriptor.Version}.");
            }
        }
    }

    private static string? Bound(string? value, int maxBytes)
    {
        if (value is null || maxBytes <= 0)
            return value is null ? null : string.Empty;
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        var result = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes)
                break;
            result.Append(rune);
            bytes += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }
}

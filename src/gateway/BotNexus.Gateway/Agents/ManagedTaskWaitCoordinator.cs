using BotNexus.Persistence.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Identifies a runtime component that can resume one class of durably parked managed tasks.
/// Implementations must fence any externally visible resume work with <see cref="ManagedTaskContinuationContext.Generation"/>.
/// </summary>
public interface IManagedTaskContinuationOwner
{
    /// <summary>Gets the stable owner key persisted with waits routed to this callback.</summary>
    string Owner { get; }

    /// <summary>Resumes the parked task for the claimed continuation generation.</summary>
    Task ContinueAsync(ManagedTaskContinuationContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Carries the durable continuation identity and generation that an owner uses to reject stale replay.
/// </summary>
public sealed record ManagedTaskContinuationContext(
    string RunId,
    string StepId,
    string WaitId,
    string IntentId,
    string Response,
    long Generation);

/// <summary>
/// Connects generic gateway owners to durable managed-task waits without retaining an executor while work is parked.
/// </summary>
public sealed class ManagedTaskWaitCoordinator
{
    private const int RecoveryBatchSize = 100;

    private readonly SqliteManagedTaskFlowLedger _ledger;
    private readonly IReadOnlyDictionary<string, IManagedTaskContinuationOwner> _owners;
    private readonly ILogger<ManagedTaskWaitCoordinator> _logger;

    /// <summary>Creates a coordinator over the host's durable task-flow ledger and registered continuation owners.</summary>
    public ManagedTaskWaitCoordinator(
        SqliteManagedTaskFlowLedger ledger,
        IEnumerable<IManagedTaskContinuationOwner> owners,
        ILogger<ManagedTaskWaitCoordinator> logger)
    {
        _ledger = ledger;
        _logger = logger;
        _owners = owners.ToDictionary(owner => owner.Owner, StringComparer.Ordinal);
        if (_owners.Keys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Managed-task continuation owner keys must not be blank.", nameof(owners));
    }

    /// <summary>Durably parks a step and returns after persistence, without retaining an executor or worker.</summary>
    public Task<ManagedTaskWaitWriteResult> ParkAsync(
        ParkManagedTaskWaitCommand command,
        CancellationToken cancellationToken = default) =>
        _ledger.ParkWaitAsync(command, cancellationToken);

    /// <summary>
    /// Atomically wakes a parked wait through the ledger and dispatches the resulting continuation when this process wins its claim.
    /// </summary>
    public async Task<ManagedTaskWaitWriteResult> WakeAsync(
        WakeManagedTaskWaitCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await _ledger.WakeWaitAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.ContinuationIntent is { } intent)
            await DispatchAsync(intent, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Claims and dispatches a bounded snapshot of pending continuation intents.</summary>
    public async Task DispatchPendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = await _ledger.GetPendingContinuationIntentsAsync(
            RecoveryBatchSize, cancellationToken).ConfigureAwait(false);
        foreach (var intent in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DispatchAsync(intent, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Invalidates generations held by an interrupted process, then claims and dispatches all currently recoverable intents.
    /// </summary>
    public async Task RecoverAndDispatchAsync(CancellationToken cancellationToken = default)
    {
        var interrupted = await _ledger.GetInProgressContinuationIntentsAsync(
            RecoveryBatchSize, cancellationToken).ConfigureAwait(false);
        foreach (var intent in interrupted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _ledger.RecoverContinuationIntentAsync(new(
                CommandId("recover", intent, intent.Generation),
                intent.RunId,
                intent.IntentId,
                intent.Generation), cancellationToken).ConfigureAwait(false);
        }

        await DispatchPendingAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DispatchAsync(
        ManagedTaskContinuationIntentRecord candidate,
        CancellationToken cancellationToken)
    {
        if (!_owners.TryGetValue(candidate.Owner, out var owner))
        {
            _logger.LogWarning(
                "Managed-task continuation {IntentId} for run {RunId} remains pending because owner {Owner} is not registered.",
                candidate.IntentId, candidate.RunId, candidate.Owner);
            return;
        }

        var claim = await _ledger.ClaimContinuationIntentAsync(new(
            CommandId("claim", candidate, candidate.Generation),
            candidate.RunId,
            candidate.IntentId,
            candidate.Generation), cancellationToken).ConfigureAwait(false);
        if (claim.Outcome != ManagedTaskLedgerWriteOutcome.Applied || claim.ContinuationIntent is not { } claimed)
            return;

        var context = new ManagedTaskContinuationContext(
            claimed.RunId,
            claimed.StepId,
            claimed.WaitId,
            claimed.IntentId,
            claimed.Response,
            claimed.Generation);
        await owner.ContinueAsync(context, cancellationToken).ConfigureAwait(false);

        var completed = await _ledger.CompleteContinuationIntentAsync(new(
            CommandId("complete", claimed, claimed.Generation),
            claimed.RunId,
            claimed.IntentId,
            claimed.Generation), CancellationToken.None).ConfigureAwait(false);
        if (completed.Outcome != ManagedTaskLedgerWriteOutcome.Applied)
        {
            throw new InvalidOperationException(
                $"Continuation '{claimed.IntentId}' lost generation '{claimed.Generation}' before completion ({completed.Outcome}).");
        }
    }

    private static string CommandId(
        string operation,
        ManagedTaskContinuationIntentRecord intent,
        long generation) =>
        $"runtime-wait:{operation}:{intent.RunId}:{intent.IntentId}:{generation}";
}

/// <summary>Reconciles interrupted and pending continuation intents when a managed-task ledger is installed.</summary>
public sealed class ManagedTaskWaitRecoveryService(IServiceProvider services) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<SqliteManagedTaskFlowLedger>() is null)
            return;

        var coordinator = services.GetRequiredService<ManagedTaskWaitCoordinator>();
        await coordinator.RecoverAndDispatchAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

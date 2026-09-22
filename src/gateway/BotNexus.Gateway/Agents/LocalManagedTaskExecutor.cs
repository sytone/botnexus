using System.Collections.Concurrent;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Adapts ledger-managed attempts to the existing sub-agent runtime. It deliberately forwards the original
/// <see cref="SubAgentSpawnRequest"/> unchanged so spawn mode, grants, denies, budgets, routing and audit retain
/// <see cref="ISubAgentManager"/>'s established behavior.
/// </summary>
public sealed class LocalManagedTaskExecutor(ISubAgentManager subAgents, IManagedTaskAttemptStore attempts) : IManagedTaskExecutor
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _attemptLocks = new(StringComparer.Ordinal);

    public async Task<ManagedTaskExecutionReceipt> SubmitAsync(ManagedTaskExecutionSpecification specification, CancellationToken ct = default)
    {
        Validate(specification);
        var gate = _attemptLocks.GetOrAdd(specification.AttemptId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The attempt identity is durably owned before capability checks or the existing executor can start.
            var reservation = await attempts.ReserveAsync(specification, ct).ConfigureAwait(false);
            var record = reservation.Record;
            if (!reservation.Created || record.ExecutionId is not null || record.CancellationRequested)
                return ToReceipt(record);

            // Persist the launch-uncertain boundary before crossing into the existing executor. If the
            // process dies after spawn but before execution-id attachment, reconciliation reports uncertainty
            // rather than claiming the work is missing or risking a duplicate relaunch.
            record = await attempts.UpdateAsync(record with
            {
                Status = ManagedTaskExecutionStatus.Uncertain,
                Detail = "Execution identity has been reserved; local launch has not yet been confirmed."
            }, CancellationToken.None).ConfigureAwait(false);

            if (specification.Isolation != ManagedTaskIsolationRequirement.InProcess)
            {
                await attempts.UpdateAsync(record with
                {
                    Status = ManagedTaskExecutionStatus.Unsupported,
                    Detail = $"Required isolation '{specification.Isolation}' is unsupported; host execution fallback is forbidden."
                }, CancellationToken.None).ConfigureAwait(false);
                throw new ManagedTaskExecutionNotSupportedException(
                    $"The local executor does not support required isolation '{specification.Isolation}'; host execution fallback is forbidden.");
            }
            if (specification.RequiredCapabilities.Count != 0)
            {
                await attempts.UpdateAsync(record with
                {
                    Status = ManagedTaskExecutionStatus.Unsupported,
                    Detail = $"Required capabilities are unsupported: {string.Join(", ", specification.RequiredCapabilities)}."
                }, CancellationToken.None).ConfigureAwait(false);
                throw new ManagedTaskExecutionNotSupportedException(
                    $"The local executor does not provide required capabilities: {string.Join(", ", specification.RequiredCapabilities)}.");
            }

            try
            {
                var info = await subAgents.SpawnAsync(specification.LocalExecution, ct).ConfigureAwait(false);
                record = await attempts.UpdateAsync(record with
                {
                    ExecutionId = info.SubAgentId,
                    Status = Map(info.Status),
                    ResultReference = info.ResultSummary,
                    Detail = null
                }, CancellationToken.None).ConfigureAwait(false);
                return ToReceipt(record);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                record = await attempts.UpdateAsync(record with
                {
                    Status = ManagedTaskExecutionStatus.Failed,
                    Detail = ex.Message
                }, CancellationToken.None).ConfigureAwait(false);
                return ToReceipt(record);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ManagedTaskExecutionReceipt?> InspectAsync(string attemptId, CancellationToken ct = default)
    {
        var record = await attempts.GetAsync(attemptId, ct).ConfigureAwait(false);
        if (record is null) return null;
        return ToReceipt(await RefreshAsync(record, ct).ConfigureAwait(false));
    }

    public async Task<ManagedTaskExecutionReceipt?> CancelAsync(string attemptId, CancellationToken ct = default)
    {
        var gate = _attemptLocks.GetOrAdd(attemptId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var record = await attempts.GetAsync(attemptId, ct).ConfigureAwait(false);
            if (record is null) return null;
            if (record.CancellationRequested) return ToReceipt(record);

            // Sticky first: a caller retry or restart never launches a second cancellation operation.
            record = await attempts.UpdateAsync(record with { CancellationRequested = true }, CancellationToken.None).ConfigureAwait(false);
            if (record.ExecutionId is null)
                return ToReceipt(await attempts.UpdateAsync(record with
                {
                    Status = ManagedTaskExecutionStatus.Cancelled,
                    Detail = "Cancellation was recorded before execution started."
                }, CancellationToken.None).ConfigureAwait(false));

            try
            {
                var killed = await subAgents.KillAsync(record.ExecutionId, record.Specification.LocalExecution.ParentSessionId, ct)
                    .WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                record = await attempts.UpdateAsync(record with
                {
                    Status = killed ? ManagedTaskExecutionStatus.Cancelled : ManagedTaskExecutionStatus.Uncertain,
                    Detail = killed ? null : "The local executor did not confirm cancellation."
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                record = await attempts.UpdateAsync(record with
                {
                    Status = ManagedTaskExecutionStatus.Uncertain,
                    Detail = $"Cancellation could not be confirmed: {ex.Message}"
                }, CancellationToken.None).ConfigureAwait(false);
            }
            return ToReceipt(record);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<ManagedTaskExecutionReceipt>> ReconcileAsync(CancellationToken ct = default)
    {
        var records = await attempts.ListAsync(ct).ConfigureAwait(false);
        var receipts = new List<ManagedTaskExecutionReceipt>(records.Count);
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            receipts.Add(ToReceipt(await RefreshAsync(record, ct).ConfigureAwait(false)));
        }
        return receipts;
    }

    private async Task<ManagedTaskAttemptRecord> RefreshAsync(ManagedTaskAttemptRecord record, CancellationToken ct)
    {
        if (record.Status is ManagedTaskExecutionStatus.Unsupported or ManagedTaskExecutionStatus.Completed or ManagedTaskExecutionStatus.Failed)
            return record;
        if (record.ExecutionId is null)
            return await attempts.UpdateAsync(record with { Status = ManagedTaskExecutionStatus.Missing }, CancellationToken.None).ConfigureAwait(false);

        try
        {
            var info = await subAgents.GetAsync(record.ExecutionId, ct).ConfigureAwait(false);
            var updated = info is null
                ? record with { Status = ManagedTaskExecutionStatus.Missing }
                : record with { Status = Map(info.Status), ResultReference = info.ResultSummary };
            return await attempts.UpdateAsync(updated, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return await attempts.UpdateAsync(record with
            {
                Status = ManagedTaskExecutionStatus.Uncertain,
                Detail = ex.Message
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static ManagedTaskExecutionStatus Map(SubAgentStatus status) => status switch
    {
        SubAgentStatus.Running => ManagedTaskExecutionStatus.Running,
        SubAgentStatus.Completed or SubAgentStatus.HandedOff => ManagedTaskExecutionStatus.Completed,
        SubAgentStatus.Killed => ManagedTaskExecutionStatus.Cancelled,
        _ => ManagedTaskExecutionStatus.Failed
    };

    private static ManagedTaskExecutionReceipt ToReceipt(ManagedTaskAttemptRecord record) => new(
        record.Specification.AttemptId,
        record.ExecutionId,
        record.Status,
        record.ResultReference,
        record.CancellationRequested,
        record.Detail);

    private static void Validate(ManagedTaskExecutionSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (specification.Version != ManagedTaskExecutionSpecification.CurrentVersion)
            throw new ManagedTaskExecutionNotSupportedException($"Managed-task execution specification version '{specification.Version}' is unsupported.");
        if (string.IsNullOrWhiteSpace(specification.AttemptId)) throw new ArgumentException("AttemptId is required.", nameof(specification));
        if (string.IsNullOrWhiteSpace(specification.TaskInputReference)) throw new ArgumentException("TaskInputReference is required.", nameof(specification));
        if (string.IsNullOrWhiteSpace(specification.FencingToken)) throw new ArgumentException("FencingToken is required.", nameof(specification));
        if (string.IsNullOrWhiteSpace(specification.OutputContract)) throw new ArgumentException("OutputContract is required.", nameof(specification));
        var now = DateTimeOffset.UtcNow;
        if (specification.Deadline <= now) throw new ArgumentException("Deadline must be in the future.", nameof(specification));
        if (specification.ResourceBounds.MaxTurns <= 0 || specification.ResourceBounds.TimeoutSeconds <= 0)
            throw new ArgumentException("Resource bounds must be positive.", nameof(specification));
        if (specification.LocalExecution.MaxTurns > specification.ResourceBounds.MaxTurns ||
            specification.LocalExecution.TimeoutSeconds > specification.ResourceBounds.TimeoutSeconds)
            throw new ArgumentException("Local execution exceeds the managed-task resource bounds.", nameof(specification));
        if (now.AddSeconds(specification.LocalExecution.TimeoutSeconds) > specification.Deadline)
            throw new ArgumentException("Local execution timeout exceeds the managed-task deadline.", nameof(specification));
    }
}

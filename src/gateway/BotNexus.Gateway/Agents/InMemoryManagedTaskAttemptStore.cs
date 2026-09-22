using System.Collections.Concurrent;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Agents;

namespace BotNexus.Gateway.Agents;

/// <summary>In-process ledger implementation for hosts that do not supply durable managed-task storage.</summary>
public sealed class InMemoryManagedTaskAttemptStore : IManagedTaskAttemptStore
{
    private readonly ConcurrentDictionary<string, ManagedTaskAttemptRecord> _records = new(StringComparer.Ordinal);

    public Task<ManagedTaskAttemptRecord?> GetAsync(string attemptId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _records.TryGetValue(attemptId, out var record);
        return Task.FromResult(record);
    }

    public Task<IReadOnlyList<ManagedTaskAttemptRecord>> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ManagedTaskAttemptRecord>>(_records.Values.ToArray());
    }

    public Task<ManagedTaskAttemptReservation> ReserveAsync(ManagedTaskExecutionSpecification specification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ct.ThrowIfCancellationRequested();
        var created = new ManagedTaskAttemptRecord(specification, null, ManagedTaskExecutionStatus.Pending);
        var record = _records.GetOrAdd(specification.AttemptId, created);
        if (!SpecificationsMatch(record.Specification, specification))
            throw new InvalidOperationException($"Managed-task attempt '{specification.AttemptId}' was already submitted with a different immutable specification.");
        return Task.FromResult(new ManagedTaskAttemptReservation(record, ReferenceEquals(record, created)));
    }

    public Task<ManagedTaskAttemptRecord> UpdateAsync(ManagedTaskAttemptRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();
        var updated = _records.AddOrUpdate(
            record.Specification.AttemptId,
            _ => throw new InvalidOperationException($"Managed-task attempt '{record.Specification.AttemptId}' has not been created."),
            (_, current) => SpecificationsMatch(current.Specification, record.Specification)
                ? MergeMonotonic(current, record)
                : throw new InvalidOperationException($"Managed-task attempt '{record.Specification.AttemptId}' cannot change its immutable specification."));
        return Task.FromResult(updated);
    }

    private static ManagedTaskAttemptRecord MergeMonotonic(ManagedTaskAttemptRecord current, ManagedTaskAttemptRecord proposed)
    {
        var cancellationRequested = current.CancellationRequested || proposed.CancellationRequested;
        var currentTerminal = current.Status is ManagedTaskExecutionStatus.Completed
            or ManagedTaskExecutionStatus.Failed
            or ManagedTaskExecutionStatus.Cancelled
            or ManagedTaskExecutionStatus.Unsupported;
        if (currentTerminal && !proposed.CancellationRequested)
            return current with { CancellationRequested = cancellationRequested };

        return proposed with
        {
            CancellationRequested = cancellationRequested,
            ExecutionId = proposed.ExecutionId ?? current.ExecutionId,
            ResultReference = proposed.ResultReference ?? current.ResultReference
        };
    }

    private static bool SpecificationsMatch(ManagedTaskExecutionSpecification left, ManagedTaskExecutionSpecification right)
        => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
}

using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>A versioned, immutable request to execute one managed-task attempt.</summary>
public sealed record ManagedTaskExecutionSpecification
{
    public const int CurrentVersion = 1;
    public required int Version { get; init; }
    public required string AttemptId { get; init; }
    public required string TaskInputReference { get; init; }
    public required SubAgentSpawnRequest LocalExecution { get; init; }
    public required DateTimeOffset Deadline { get; init; }
    public required ManagedTaskResourceBounds ResourceBounds { get; init; }
    public required IReadOnlyList<string> RequiredCapabilities { get; init; }
    public required string OutputContract { get; init; }
    public required string FencingToken { get; init; }
    public ManagedTaskIsolationRequirement Isolation { get; init; } = ManagedTaskIsolationRequirement.InProcess;
}

/// <summary>Host-enforced resource ceilings supplied by the managed-task ledger.</summary>
public sealed record ManagedTaskResourceBounds(int MaxTurns, int TimeoutSeconds);

public enum ManagedTaskIsolationRequirement
{
    InProcess,
    Container,
    Remote
}

public enum ManagedTaskExecutionStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
    Missing,
    Unsupported,
    Uncertain
}

/// <summary>A stable receipt; the attempt id is the idempotency key and execution id names the underlying run.</summary>
public sealed record ManagedTaskExecutionReceipt(
    string AttemptId,
    string? ExecutionId,
    ManagedTaskExecutionStatus Status,
    string? ResultReference = null,
    bool CancellationRequested = false,
    string? Detail = null);

public sealed record ManagedTaskAttemptRecord(
    ManagedTaskExecutionSpecification Specification,
    string? ExecutionId,
    ManagedTaskExecutionStatus Status,
    string? ResultReference = null,
    bool CancellationRequested = false,
    string? Detail = null);

public sealed record ManagedTaskAttemptReservation(ManagedTaskAttemptRecord Record, bool Created);

public interface IManagedTaskExecutor
{
    Task<ManagedTaskExecutionReceipt> SubmitAsync(ManagedTaskExecutionSpecification specification, CancellationToken ct = default);
    Task<ManagedTaskExecutionReceipt?> InspectAsync(string attemptId, CancellationToken ct = default);
    Task<ManagedTaskExecutionReceipt?> CancelAsync(string attemptId, CancellationToken ct = default);
    Task<IReadOnlyList<ManagedTaskExecutionReceipt>> ReconcileAsync(CancellationToken ct = default);
}

/// <summary>
/// Ledger-owned persistence seam. Create must atomically retain the first specification for an attempt id and
/// return it on retries; an implementation must reject a different immutable specification for that id.
/// </summary>
public interface IManagedTaskAttemptStore
{
    Task<ManagedTaskAttemptRecord?> GetAsync(string attemptId, CancellationToken ct = default);
    Task<IReadOnlyList<ManagedTaskAttemptRecord>> ListAsync(CancellationToken ct = default);
    Task<ManagedTaskAttemptReservation> ReserveAsync(ManagedTaskExecutionSpecification specification, CancellationToken ct = default);
    Task<ManagedTaskAttemptRecord> UpdateAsync(ManagedTaskAttemptRecord record, CancellationToken ct = default);
}

public sealed class ManagedTaskExecutionNotSupportedException(string message) : NotSupportedException(message);

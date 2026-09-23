using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Persistence.Sqlite;

/// <summary>An immutable sequence whose equality reflects its values and that supports collection expressions.</summary>
[CollectionBuilder(typeof(ManagedTaskValueListBuilder), nameof(ManagedTaskValueListBuilder.Create))]
[JsonConverter(typeof(ManagedTaskValueListJsonConverterFactory))]
public sealed class ManagedTaskValueList<T> : IReadOnlyList<T>, IEquatable<ManagedTaskValueList<T>>
{
    private readonly T[] _values;

    /// <summary>Creates an empty immutable value sequence.</summary>
    public ManagedTaskValueList()
    {
        _values = [];
    }

    /// <summary>Copies values so callers cannot mutate the persisted contract after construction.</summary>
    public ManagedTaskValueList(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values.ToArray();
    }

    /// <inheritdoc />
    public int Count => _values.Length;

    /// <inheritdoc />
    public T this[int index] => _values[index];

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_values).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => _values.GetEnumerator();

    /// <inheritdoc />
    public bool Equals(ManagedTaskValueList<T>? other) =>
        other is not null && _values.AsSpan().SequenceEqual(other._values);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ManagedTaskValueList<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in _values)
            hash.Add(value);
        return hash.ToHashCode();
    }
}

/// <summary>Builds immutable value sequences directly from collection expressions.</summary>
public static class ManagedTaskValueListBuilder
{
    /// <summary>Copies collection-expression values into immutable storage.</summary>
    public static ManagedTaskValueList<T> Create<T>(ReadOnlySpan<T> values) => new(values.ToArray());
}

/// <summary>Serializes immutable value sequences with their established JSON array representation.</summary>
public sealed class ManagedTaskValueListJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ManagedTaskValueList<>);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var elementType = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter)Activator.CreateInstance(
            typeof(ManagedTaskValueListJsonConverter<>).MakeGenericType(elementType))!;
    }

    private sealed class ManagedTaskValueListJsonConverter<T> : JsonConverter<ManagedTaskValueList<T>>
    {
        public override ManagedTaskValueList<T>? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var values = JsonSerializer.Deserialize<T[]>(ref reader, options);
            return values is null ? null : new ManagedTaskValueList<T>(values);
        }

        public override void Write(
            Utf8JsonWriter writer,
            ManagedTaskValueList<T> value,
            JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
}

/// <summary>Identifies optional source metadata for a run authored outside the ledger.</summary>
public sealed record ManagedTaskAuthoredDefinition(string Name, int Version, string SourceReference);

/// <summary>Caps retry and cancellation behavior without describing workflow topology.</summary>
public sealed record ManagedTaskPolicyBounds(int MaxAttemptsPerStep, TimeSpan CancellationGrace);

/// <summary>Caps resources that execution code may consume for a run or step.</summary>
public sealed record ManagedTaskResourceBounds(int MaxTurns, TimeSpan Timeout, int MaxConcurrentAttempts);

/// <summary>Immutable specification of one independently managed task step.</summary>
public sealed record ManagedTaskStepSpecification(
    string StepId,
    string TaskReference,
    ManagedTaskPolicyBounds PolicyBounds,
    ManagedTaskResourceBounds ResourceBounds);

/// <summary>Immutable input and bounds retained for the lifetime of a managed task run.</summary>
public sealed record ManagedTaskRunSpecification(
    string RunId,
    string Input,
    string InputReference,
    ManagedTaskPolicyBounds PolicyBounds,
    ManagedTaskResourceBounds ResourceBounds,
    ManagedTaskAuthoredDefinition? AuthoredDefinition,
    ManagedTaskValueList<ManagedTaskStepSpecification> Steps);

/// <summary>Lifecycle states persisted for a managed task run.</summary>
public enum ManagedTaskRunStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Lifecycle states persisted for one execution attempt.</summary>
public enum ManagedTaskAttemptStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
    UnknownSideEffect,
}

/// <summary>Append-only fact types emitted by accepted ledger commands.</summary>
public enum ManagedTaskEventType
{
    RunCreated,
    RunTransitioned,
    AttemptAdmitted,
    AttemptCommitted,
    RunCancelled,
}

/// <summary>Stable outcomes returned by fenced ledger writes.</summary>
public enum ManagedTaskLedgerWriteOutcome
{
    Applied,
    Duplicate,
    RevisionConflict,
    EpochConflict,
    AttemptNotFound,
    Cancelled,
    Terminal,
    RunNotFound,
    StepNotFound,
}

/// <summary>Observer points around the durability boundary, used to verify crash semantics.</summary>
public enum ManagedTaskFlowCommitPoint
{
    BeforeCommit,
    AfterCommit,
}

/// <summary>Observes the exact commit boundary without taking ownership of the transaction.</summary>
public interface IManagedTaskFlowCommitObserver
{
    /// <summary>Runs synchronously at the requested durability boundary.</summary>
    void OnCommitPoint(ManagedTaskFlowCommitPoint observed, string commandId);
}

/// <summary>Creates a run and its immutable step projections.</summary>
public sealed record CreateManagedTaskRunCommand(string CommandId, ManagedTaskRunSpecification Specification);

/// <summary>Moves a run when its revision still matches the caller's observation.</summary>
public sealed record TransitionManagedTaskRunCommand(
    string CommandId,
    string RunId,
    long ExpectedRevision,
    ManagedTaskRunStatus Status);

/// <summary>Admits a new attempt when the step projection is still current.</summary>
public sealed record AdmitManagedTaskAttemptCommand(
    string CommandId,
    string RunId,
    string StepId,
    long ExpectedStepRevision,
    string AttemptId);

/// <summary>Records a terminal attempt result under both revision and epoch fences.</summary>
public sealed record CommitManagedTaskAttemptCommand(
    string CommandId,
    string RunId,
    string StepId,
    string AttemptId,
    long ExpectedStepRevision,
    long ExpectedAttemptEpoch,
    ManagedTaskAttemptStatus Status,
    string? ResultReference);

/// <summary>Makes cancellation sticky for a run and all of its active attempts.</summary>
public sealed record CancelManagedTaskRunCommand(
    string CommandId,
    string RunId,
    long ExpectedRunRevision,
    string Reason);

/// <summary>Durable run projection including the immutable creation specification.</summary>
public sealed record ManagedTaskRunRecord(
    string RunId,
    ManagedTaskRunSpecification Specification,
    ManagedTaskRunStatus Status,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? CancellationReason);

/// <summary>Durable step projection anchored to its immutable specification.</summary>
public sealed record ManagedTaskStepRecord(
    string RunId,
    string StepId,
    ManagedTaskStepSpecification Specification,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Durable attempt projection with a monotonically increasing per-step epoch.</summary>
public sealed record ManagedTaskAttemptRecord(
    string RunId,
    string StepId,
    string AttemptId,
    long Epoch,
    ManagedTaskAttemptStatus Status,
    string? ResultReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Indicates that no later result may replace this attempt state.</summary>
    public bool IsTerminal => Status != ManagedTaskAttemptStatus.Running;

    /// <summary>Indicates whether policy may admit another attempt after this result.</summary>
    public bool IsRetryable => Status == ManagedTaskAttemptStatus.Failed;
}

/// <summary>Consistent read of all projections belonging to one run.</summary>
public sealed record ManagedTaskRunSnapshot(
    ManagedTaskRunRecord Run,
    ManagedTaskValueList<ManagedTaskStepRecord> Steps,
    ManagedTaskValueList<ManagedTaskAttemptRecord> Attempts);

/// <summary>Result of a write together with the durable projection, when the run exists.</summary>
public sealed record ManagedTaskLedgerWriteResult(
    ManagedTaskLedgerWriteOutcome Outcome,
    ManagedTaskRunSnapshot? Snapshot);

/// <summary>One immutable fact in a run's ordered event history.</summary>
public sealed record ManagedTaskEventRecord(
    long Sequence,
    string RunId,
    string CommandId,
    ManagedTaskEventType Type,
    DateTimeOffset OccurredAt);

/// <summary>Signals command identity reuse or an immutable run specification conflict.</summary>
public sealed class ManagedTaskLedgerConflictException(string message) : InvalidOperationException(message);

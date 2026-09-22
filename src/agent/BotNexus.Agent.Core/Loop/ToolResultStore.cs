using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vogen;

namespace BotNexus.Agent.Core.Loop;

/// <summary>Unguessable identity of a retained tool result.</summary>
[ValueObject<string>(conversions: Conversions.SystemTextJson)]
public readonly partial struct ToolResultId
{
    private static Validation Validate(string value) =>
        value is { Length: >= 46 } && value.StartsWith("tr_", StringComparison.Ordinal)
            ? Validation.Ok
            : Validation.Invalid("ToolResultId must be an unguessable tr_ result identifier.");

    private static string NormalizeInput(string input) => input is null ? input! : input.Trim();

    /// <summary>Parses a serialized result identity.</summary>
    public static ToolResultId Parse(string value) => From(value);

    internal static ToolResultId Create()
    {
        Span<byte> entropy = stackalloc byte[32];
        RandomNumberGenerator.Fill(entropy);
        var token = Convert.ToBase64String(entropy).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return From($"tr_{token}");
    }
}

/// <summary>Logical shape of a stored result.</summary>
public enum ToolResultKind { Scalar, Text, Object, Table, Blob }

/// <summary>Whether later projections must remain tainted as foreign content.</summary>
public enum ToolResultProvenance { LocalTrusted, ForeignUntrusted, Unknown }

/// <summary>Whether the stored bytes represent the complete source result.</summary>
public enum ToolResultCompleteness { Complete, Partial }

/// <summary>Retention promise made by the store.</summary>
public enum ToolResultRetention { Volatile, Durable }

/// <summary>Operations supported by this store implementation.</summary>
public enum ToolResultOperation { Read, Project }

/// <summary>Terminal state represented by a receipt.</summary>
public enum ToolResultTerminalStatus { Available, Complete, Failed }

/// <summary>Distinct outcomes from looking up a result.</summary>
public enum ToolResultReadStatus { Ok, Unknown, AccessDenied, Expired, Evicted, Corrupt, StaleRevision }

/// <summary>Effective ownership boundary for a result.</summary>
public sealed record ToolResultScope(
    string WorldId,
    string AgentId,
    string ConversationId,
    string SessionId,
    string PolicyId);

/// <summary>Caller-supplied safe metadata describing payload bytes.</summary>
public sealed record ToolResultDescriptor(
    ToolResultKind Kind,
    string MediaType,
    string? Schema,
    ToolResultProvenance Provenance,
    string SourceTool,
    string SourceCallId,
    long? Count,
    ToolResultCompleteness Completeness,
    ToolResultRetention Retention);

/// <summary>Capacity and lifetime bounds for an in-process result store.</summary>
public sealed record ToolResultStoreOptions(
    int MaxEntries = 64,
    long MaxTotalBytes = 32L * 1024 * 1024,
    long MaxResultBytes = 16L * 1024 * 1024,
    TimeSpan? DefaultRetention = null)
{
    internal TimeSpan EffectiveRetention => DefaultRetention ?? TimeSpan.FromMinutes(30);
}

/// <summary>Compact, model-safe identity and metadata for retained payload bytes.</summary>
public sealed record ToolResultReceipt(
    ToolResultId ResultId,
    long Revision,
    ToolResultKind Kind,
    string MediaType,
    string? Schema,
    string SourceTool,
    string SourceCallId,
    long? Count,
    long SizeBytes,
    ToolResultCompleteness Completeness,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    ToolResultRetention Retention,
    ToolResultProvenance Provenance,
    string IntegritySha256,
    ToolResultTerminalStatus TerminalStatus,
    IReadOnlyList<ToolResultOperation> SupportedOperations)
{
    /// <summary>Produces a bounded projection containing metadata but never payload bytes.</summary>
    public string ToModelProjection() => JsonSerializer.Serialize(new
    {
        result_id = ResultId.Value,
        revision = Revision,
        kind = Kind.ToString().ToLowerInvariant(),
        media_type = MediaType,
        schema = Schema,
        source_tool = SourceTool,
        source_call = SourceCallId,
        count = Count,
        size_bytes = SizeBytes,
        completeness = Completeness.ToString().ToLowerInvariant(),
        expires_at = ExpiresAt,
        retention = Retention.ToString().ToLowerInvariant(),
        provenance = Provenance.ToString().ToLowerInvariant(),
        terminal_status = TerminalStatus.ToString().ToLowerInvariant(),
        operations = SupportedOperations.Select(operation => operation.ToString().ToLowerInvariant())
    });
}

/// <summary>Payload lookup result. Non-success outcomes never expose payload or metadata.</summary>
public sealed record ToolResultReadResult(
    ToolResultReadStatus Status,
    ReadOnlyMemory<byte> Payload,
    ToolResultReceipt? Receipt);

/// <summary>
/// Bounded typed store for complete, already-redacted tool-result bytes. Payloads are scoped and
/// represented to the model by compact receipts rather than by copying the complete value into the
/// prompt. Durable filesystem persistence is intentionally a separate implementation of this
/// contract; this implementation provides the process-local foundation and continuation bridge.
/// </summary>
public sealed class ToolResultStore
{
    private static readonly IReadOnlyList<ToolResultOperation> Operations =
        [ToolResultOperation.Read, ToolResultOperation.Project];

    private readonly ToolResultStoreOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<ToolResultId, Entry> _entries = [];
    private readonly Dictionary<ToolResultId, ToolResultReadStatus> _tombstones = [];
    private readonly Queue<ToolResultId> _order = [];
    private long _totalBytes;

    /// <summary>Creates an in-process bounded store.</summary>
    public ToolResultStore(ToolResultStoreOptions? options = null, Func<DateTimeOffset>? clock = null)
    {
        _options = options ?? new ToolResultStoreOptions();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (_options.MaxEntries <= 0 || _options.MaxTotalBytes <= 0 || _options.MaxResultBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Result-store capacity bounds must be positive.");
        }
    }

    /// <summary>Stores complete, already-redacted bytes and returns their compact receipt.</summary>
    public ToolResultReceipt Store(ReadOnlySpan<byte> payload, ToolResultDescriptor descriptor, ToolResultScope scope)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateScope(scope);
        ValidateDescriptor(descriptor);
        if (payload.Length > _options.MaxResultBytes || payload.Length > _options.MaxTotalBytes)
        {
            throw new InvalidOperationException("Tool result exceeds the configured store capacity.");
        }

        var now = _clock();
        var id = ToolResultId.Create();
        var bytes = payload.ToArray();
        var receipt = new ToolResultReceipt(
            id,
            1,
            descriptor.Kind,
            descriptor.MediaType,
            descriptor.Schema,
            descriptor.SourceTool,
            descriptor.SourceCallId,
            descriptor.Count,
            bytes.LongLength,
            descriptor.Completeness,
            now,
            now.Add(_options.EffectiveRetention),
            descriptor.Retention,
            descriptor.Provenance,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            descriptor.Completeness == ToolResultCompleteness.Complete
                ? ToolResultTerminalStatus.Complete
                : ToolResultTerminalStatus.Available,
            Operations);

        lock (_gate)
        {
            EnsureCapacity(bytes.LongLength, descriptor.Retention);
            _entries.Add(id, new Entry(bytes, scope, receipt));
            _order.Enqueue(id);
            _totalBytes += bytes.LongLength;
        }

        return receipt;
    }

    /// <summary>Reads a result only when identity, revision, scope, integrity, and lifetime match.</summary>
    public ToolResultReadResult Read(ToolResultId id, long revision, ToolResultScope scope)
    {
        ValidateScope(scope);
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                return Failure(_tombstones.GetValueOrDefault(id, ToolResultReadStatus.Unknown));
            }

            if (entry.Scope != scope)
            {
                return Failure(ToolResultReadStatus.AccessDenied);
            }

            if (entry.Receipt.Revision != revision)
            {
                return Failure(ToolResultReadStatus.StaleRevision);
            }

            if (_clock() >= entry.Receipt.ExpiresAt)
            {
                Remove(id, entry, ToolResultReadStatus.Expired);
                return Failure(ToolResultReadStatus.Expired);
            }

            var integrity = Convert.ToHexString(SHA256.HashData(entry.Payload)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(integrity),
                    Encoding.ASCII.GetBytes(entry.Receipt.IntegritySha256)))
            {
                Remove(id, entry, ToolResultReadStatus.Corrupt);
                return Failure(ToolResultReadStatus.Corrupt);
            }

            return new ToolResultReadResult(ToolResultReadStatus.Ok, entry.Payload, entry.Receipt);
        }
    }

    /// <summary>Reads receipt metadata without returning payload bytes.</summary>
    public ToolResultReadResult GetReceipt(ToolResultId id, ToolResultScope scope)
    {
        ValidateScope(scope);
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                return Failure(_tombstones.GetValueOrDefault(id, ToolResultReadStatus.Unknown));
            }

            if (entry.Scope != scope)
            {
                return Failure(ToolResultReadStatus.AccessDenied);
            }

            if (_clock() >= entry.Receipt.ExpiresAt)
            {
                Remove(id, entry, ToolResultReadStatus.Expired);
                return Failure(ToolResultReadStatus.Expired);
            }

            return new ToolResultReadResult(ToolResultReadStatus.Ok, ReadOnlyMemory<byte>.Empty, entry.Receipt);
        }
    }

    private void EnsureCapacity(long incomingBytes, ToolResultRetention retention)
    {
        while (_entries.Count >= _options.MaxEntries || _totalBytes + incomingBytes > _options.MaxTotalBytes)
        {
            Entry? candidate = null;
            ToolResultId? candidateId = null;
            while (_order.Count > 0)
            {
                var queuedId = _order.Dequeue();
                if (_entries.TryGetValue(queuedId, out candidate) &&
                    candidate.Receipt.Retention == ToolResultRetention.Volatile)
                {
                    candidateId = queuedId;
                    break;
                }

                candidate = null;
            }

            if (candidate is null || candidateId is null)
            {
                throw new InvalidOperationException(
                    retention == ToolResultRetention.Durable
                        ? "Durable tool result cannot be stored without violating configured capacity."
                        : "Tool result store is saturated by durable results.");
            }

            Remove(candidateId.Value, candidate, ToolResultReadStatus.Evicted);
        }
    }

    private void Remove(ToolResultId id, Entry entry, ToolResultReadStatus status)
    {
        if (_entries.Remove(id))
        {
            _totalBytes -= entry.Payload.LongLength;
            _tombstones[id] = status;
            while (_tombstones.Count > _options.MaxEntries * 2)
            {
                _tombstones.Remove(_tombstones.Keys.First());
            }
        }
    }

    private static ToolResultReadResult Failure(ToolResultReadStatus status) =>
        new(status, ReadOnlyMemory<byte>.Empty, null);

    private static void ValidateScope(ToolResultScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(scope.WorldId) || string.IsNullOrWhiteSpace(scope.AgentId) ||
            string.IsNullOrWhiteSpace(scope.ConversationId) || string.IsNullOrWhiteSpace(scope.SessionId) ||
            string.IsNullOrWhiteSpace(scope.PolicyId))
        {
            throw new ArgumentException("Every tool-result scope component is required.", nameof(scope));
        }
    }

    private static void ValidateDescriptor(ToolResultDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.MediaType) ||
            string.IsNullOrWhiteSpace(descriptor.SourceTool) ||
            string.IsNullOrWhiteSpace(descriptor.SourceCallId))
        {
            throw new ArgumentException("Media type, source tool, and source call are required.", nameof(descriptor));
        }
    }

    private sealed record Entry(byte[] Payload, ToolResultScope Scope, ToolResultReceipt Receipt);
}

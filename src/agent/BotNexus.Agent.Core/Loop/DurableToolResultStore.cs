using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// Filesystem-backed result store for durable, already-redacted tool-result bytes. The configured
/// root is an artifact location supplied by the host; payload bytes are never placed in metadata.
/// </summary>
public sealed class DurableToolResultStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyList<ToolResultOperation> Operations =
        [ToolResultOperation.Read, ToolResultOperation.Project];

    private readonly string _root;
    private readonly ToolResultStoreOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _gate = new();

    /// <summary>Creates a store rooted at an explicitly configured artifact directory.</summary>
    public DurableToolResultStore(
        string artifactRoot,
        ToolResultStoreOptions? options = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRoot);
        _root = Path.GetFullPath(artifactRoot);
        _options = options ?? new ToolResultStoreOptions();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (_options.MaxEntries <= 0 || _options.MaxTotalBytes <= 0 || _options.MaxResultBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Result-store capacity bounds must be positive.");
        }

        Directory.CreateDirectory(_root);
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException("The result-store root cannot be a reparse point.", nameof(artifactRoot));
        }
    }

    /// <summary>Atomically stores durable payload bytes and metadata as separate files.</summary>
    public ToolResultReceipt Store(ReadOnlySpan<byte> payload, ToolResultDescriptor descriptor, ToolResultScope scope)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateScope(scope);
        ValidateDescriptor(descriptor);
        if (descriptor.Retention != ToolResultRetention.Durable)
        {
            throw new ArgumentException("The durable store accepts only durable results.", nameof(descriptor));
        }

        if (payload.Length > _options.MaxResultBytes || payload.Length > _options.MaxTotalBytes)
        {
            throw new InvalidOperationException("Tool result exceeds the configured durable-store capacity.");
        }

        lock (_gate)
        {
            var current = LoadRecords(removeInvalid: true);
            var currentBytes = current.Sum(record => record.Receipt.SizeBytes);
            if (current.Count >= _options.MaxEntries || currentBytes + payload.Length > _options.MaxTotalBytes)
            {
                throw new InvalidOperationException("Durable tool result cannot be stored without violating configured capacity.");
            }

            var id = ToolResultId.Create();
            var now = _clock();
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
            var record = new DurableRecord(scope, receipt);

            WriteAtomic(PayloadPath(id), bytes);
            try
            {
                WriteAtomic(MetadataPath(id), JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions));
            }
            catch
            {
                File.Delete(PayloadPath(id));
                throw;
            }

            return receipt;
        }
    }

    /// <summary>Reads a durable result after validating scope, revision, expiry, size, and integrity.</summary>
    public ToolResultReadResult Read(ToolResultId id, long revision, ToolResultScope scope)
    {
        ValidateScope(scope);
        lock (_gate)
        {
            var metadataPath = MetadataPath(id);
            if (!File.Exists(metadataPath))
            {
                return Failure(ToolResultReadStatus.Unknown);
            }

            DurableRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<DurableRecord>(File.ReadAllBytes(metadataPath), JsonOptions);
            }
            catch (JsonException)
            {
                return Failure(ToolResultReadStatus.Corrupt);
            }

            if (record is null || record.Receipt.ResultId != id)
            {
                return Failure(ToolResultReadStatus.Corrupt);
            }

            if (record.Scope != scope)
            {
                return Failure(ToolResultReadStatus.AccessDenied);
            }

            if (record.Receipt.Revision != revision)
            {
                return Failure(ToolResultReadStatus.StaleRevision);
            }

            if (_clock() >= record.Receipt.ExpiresAt)
            {
                Delete(record.Receipt.ResultId);
                return Failure(ToolResultReadStatus.Expired);
            }

            var payloadPath = PayloadPath(id);
            if (!File.Exists(payloadPath))
            {
                return Failure(ToolResultReadStatus.Corrupt);
            }

            var payload = File.ReadAllBytes(payloadPath);
            if (payload.LongLength != record.Receipt.SizeBytes)
            {
                return Failure(ToolResultReadStatus.Corrupt);
            }

            var actualIntegrity = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualIntegrity),
                    Encoding.ASCII.GetBytes(record.Receipt.IntegritySha256)))
            {
                return Failure(ToolResultReadStatus.Corrupt);
            }

            return new ToolResultReadResult(ToolResultReadStatus.Ok, payload, record.Receipt);
        }
    }

    private List<DurableRecord> LoadRecords(bool removeInvalid)
    {
        var records = new List<DurableRecord>();
        foreach (var path in Directory.EnumerateFiles(_root, "tr_*.json", SearchOption.TopDirectoryOnly)
                     .Take(_options.MaxEntries + 1))
        {
            try
            {
                var record = JsonSerializer.Deserialize<DurableRecord>(File.ReadAllBytes(path), JsonOptions);
                if (record is null)
                {
                    continue;
                }

                if (_clock() >= record.Receipt.ExpiresAt)
                {
                    if (removeInvalid)
                    {
                        Delete(record.Receipt.ResultId);
                    }
                    continue;
                }

                records.Add(record);
            }
            catch (JsonException)
            {
            }
        }

        return records;
    }

    private void WriteAtomic(string destination, ReadOnlySpan<byte> content)
    {
        var temp = Path.Combine(_root, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, destination);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    private void Delete(ToolResultId id)
    {
        File.Delete(MetadataPath(id));
        File.Delete(PayloadPath(id));
    }

    private string MetadataPath(ToolResultId id) => Path.Combine(_root, $"{id.Value}.json");
    private string PayloadPath(ToolResultId id) => Path.Combine(_root, $"{id.Value}.payload");

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

    private sealed record DurableRecord(ToolResultScope Scope, ToolResultReceipt Receipt);
}

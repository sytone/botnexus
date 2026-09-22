using System.Buffers;
using System.Text;

namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// Why a read against a continuation handle did not return a slice (#2760).
/// </summary>
/// <remarks>
/// Each failure gets its own discriminator rather than collapsing into "no data". An unknown handle
/// and an out-of-range offset are different operator problems - the first means the payload was
/// evicted or never existed, the second means the caller mis-tracked its own cursor - and a caller
/// that cannot tell them apart cannot choose between "re-run the tool" and "fix the offset".
/// </remarks>
public enum ToolOutputContinuationStatus
{
    /// <summary>A slice was returned.</summary>
    Ok = 0,

    /// <summary>No payload is registered under the handle: it never existed, or it was evicted.</summary>
    UnknownHandle = 1,

    /// <summary>The handle is known but the requested offset lies outside the stored payload.</summary>
    OffsetOutOfRange = 2
}

/// <summary>
/// One slice of a stored oversized tool payload.
/// </summary>
/// <param name="Status">Whether <paramref name="Text"/> is meaningful, and if not, why.</param>
/// <param name="Text">The retained slice, cut on a UTF-8 rune boundary.</param>
/// <param name="NextOffset">The byte offset to pass on the next call.</param>
/// <param name="TotalBytes">Total UTF-8 byte length of the stored payload.</param>
/// <param name="IsComplete">True when this slice reaches the end of the payload.</param>
public sealed record ToolOutputContinuationSlice(
    ToolOutputContinuationStatus Status,
    string Text,
    long NextOffset,
    long TotalBytes,
    bool IsComplete);

/// <summary>
/// Holds the full text of tool results that exceeded the output budget, so the truncated projection
/// the model receives carries a handle the model can page through instead of losing the data
/// (issue #2760).
/// </summary>
/// <remarks>
/// <para>
/// Before #2760 an oversized result was truncated with a marker and the omitted bytes were simply
/// gone: the forensics window showed the same 67,916-byte call retried four times unchanged, because
/// the model had no way to reach the remainder and the suggested remedy was not a parameter of the
/// surface it had called. Truncation without a continuation handle turns a large-but-valid result
/// into zero information beyond the prefix.
/// </para>
/// <para>
/// The store is deliberately bounded in BOTH entry count and total bytes and evicts oldest-first. A
/// recovery aid for oversized payloads that itself grows without limit would be a memory leak
/// proportional to exactly the traffic it exists to handle. Eviction is visible rather than silent:
/// a read against an evicted handle returns <see cref="ToolOutputContinuationStatus.UnknownHandle"/>,
/// which tells the caller to re-run the tool with a narrower scope.
/// </para>
/// </remarks>
public sealed class ToolOutputContinuationStore
{
    /// <summary>Default maximum number of retained payloads.</summary>
    public const int DefaultMaxEntries = 8;

    /// <summary>Default maximum total retained bytes across all entries (32 MiB).</summary>
    public const long DefaultMaxTotalBytes = 32L * 1024 * 1024;

    // Static fields initialize in declaration order. The compatibility scope must exist before
    // Shared invokes the parameterless constructor; declaring it afterward poisons the type
    // initializer with a null scope and breaks every ordinary tool result in the process.
    private static readonly ToolResultScope LegacyScope = new(
        "legacy-world",
        "legacy-agent",
        "legacy-conversation",
        "legacy-session",
        "legacy-continuation-policy");

    /// <summary>
    /// The ambient store used by <see cref="ToolOutputBudget"/> when no explicit store is supplied.
    /// </summary>
    /// <remarks>
    /// The budget is applied from a static seam (<c>ToolOutputBudget.Apply</c>) reached from every
    /// tool call in the process, and the continuation tool that reads the payload back is
    /// constructed by the tool factory with no line of sight to the executor. A single shared,
    /// bounded instance is what lets those two ends meet without threading a new dependency through
    /// every composition site; tests pass their own instance to stay isolated.
    /// </remarks>
    public static ToolOutputContinuationStore Shared { get; } = new();

    private readonly ToolResultStore _store;
    private readonly ToolResultScope _scope;
    private readonly int _maxAliases;
    private readonly Lock _aliasGate = new();
    private readonly Dictionary<string, ToolResultId> _aliases = new(StringComparer.Ordinal);
    private readonly Queue<string> _aliasOrder = new();

    /// <summary>Creates a compatibility store with the given capacity bounds.</summary>
    public ToolOutputContinuationStore(int maxEntries = DefaultMaxEntries, long maxTotalBytes = DefaultMaxTotalBytes)
        : this(
            new ToolResultStore(new ToolResultStoreOptions(
                maxEntries > 0 ? maxEntries : DefaultMaxEntries,
                maxTotalBytes > 0 ? maxTotalBytes : DefaultMaxTotalBytes,
                maxTotalBytes > 0 ? maxTotalBytes : DefaultMaxTotalBytes)),
            LegacyScope,
            maxEntries > 0 ? maxEntries : DefaultMaxEntries)
    {
    }

    /// <summary>Creates a text continuation adapter over the shared typed result store.</summary>
    public ToolOutputContinuationStore(ToolResultStore store, ToolResultScope scope)
        : this(store, scope, DefaultMaxEntries)
    {
    }

    private ToolOutputContinuationStore(ToolResultStore store, ToolResultScope scope, int maxAliases)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _maxAliases = maxAliases;
    }

    /// <summary>
    /// Registers the full payload of an oversized tool result and returns the handle that retrieves it.
    /// </summary>
    /// <param name="fullText">The complete text of the result, before truncation.</param>
    /// <param name="toolName">The tool that produced it, recorded for diagnostics.</param>
    public string Store(string fullText, string? toolName = null)
    {
        ArgumentNullException.ThrowIfNull(fullText);

        var bytes = Encoding.UTF8.GetBytes(fullText);
        var receipt = _store.Store(
            bytes,
            new ToolResultDescriptor(
                ToolResultKind.Text,
                "text/plain; charset=utf-8",
                Schema: null,
                ToolResultProvenance.Unknown,
                toolName ?? "unknown-tool",
                "legacy-continuation",
                Count: null,
                ToolResultCompleteness.Complete,
                ToolResultRetention.Volatile),
            _scope);

        // Preserve the established continuation-handle contract while the typed store retains its
        // distinct tr_ identity. Exposing tr_ here would make existing tools and persisted guidance
        // parse an empty handle, even though the payload was stored successfully.
        var handle = $"toc_{Guid.NewGuid():N}";
        lock (_aliasGate)
        {
            while (_aliases.Count >= _maxAliases && _aliasOrder.TryDequeue(out var oldest))
                _aliases.Remove(oldest);
            _aliases.Add(handle, receipt.ResultId);
            _aliasOrder.Enqueue(handle);
        }

        return handle;
    }

    /// <summary>
    /// Reads the slice of a stored payload starting at <paramref name="offset"/>.
    /// </summary>
    /// <param name="handle">A handle previously returned by <see cref="Store"/>.</param>
    /// <param name="offset">Byte offset to resume from.</param>
    /// <param name="maxBytes">Maximum UTF-8 bytes to return; non-positive means the whole remainder.</param>
    public ToolOutputContinuationSlice Read(string? handle, long offset, int maxBytes)
    {
        if (handle is null)
            return new ToolOutputContinuationSlice(ToolOutputContinuationStatus.UnknownHandle, string.Empty, 0, 0, false);

        ToolResultId resultId;
        lock (_aliasGate)
        {
            if (!_aliases.TryGetValue(handle, out resultId))
                return new ToolOutputContinuationSlice(ToolOutputContinuationStatus.UnknownHandle, string.Empty, 0, 0, false);
        }

        var receiptResult = _store.GetReceipt(resultId, _scope);
        if (receiptResult.Status != ToolResultReadStatus.Ok || receiptResult.Receipt is null)
        {
            return new ToolOutputContinuationSlice(ToolOutputContinuationStatus.UnknownHandle, string.Empty, 0, 0, false);
        }

        var result = _store.Read(resultId, receiptResult.Receipt.Revision, _scope);
        if (result.Status != ToolResultReadStatus.Ok)
        {
            return new ToolOutputContinuationSlice(ToolOutputContinuationStatus.UnknownHandle, string.Empty, 0, 0, false);
        }

        var bytes = result.Payload.ToArray();
        var total = bytes.LongLength;
        if (offset < 0 || offset > total)
        {
            return new ToolOutputContinuationSlice(ToolOutputContinuationStatus.OffsetOutOfRange, string.Empty, offset, total, false);
        }

        var available = total - offset;
        var take = maxBytes > 0 ? Math.Min(maxBytes, available) : available;
        var count = RuneSafeLength(bytes.AsSpan((int)offset, (int)take));
        var text = Encoding.UTF8.GetString(bytes, (int)offset, count);
        var next = offset + count;

        return new ToolOutputContinuationSlice(
            ToolOutputContinuationStatus.Ok,
            text,
            next,
            total,
            next >= total);
    }

    /// <summary>
    /// Returns the longest prefix of <paramref name="span"/> that ends on a UTF-8 rune boundary.
    /// </summary>
    /// <remarks>
    /// A slice that ends mid-sequence would surface to the model as a U+FFFD replacement character,
    /// and - worse for a paging protocol - the next slice would start mid-sequence too, corrupting
    /// one character at every chunk seam rather than only at the final cut.
    /// </remarks>
    private static int RuneSafeLength(ReadOnlySpan<byte> span)
    {
        var consumed = 0;
        while (consumed < span.Length)
        {
            var status = Rune.DecodeFromUtf8(span[consumed..], out _, out var bytesConsumed);
            if (status != OperationStatus.Done)
            {
                break;
            }

            consumed += bytesConsumed;
        }

        return consumed;
    }

}

using System.Diagnostics.Metrics;
using System.Text;
using BotNexus.Agent.Core.Types;

namespace BotNexus.Agent.Core.Loop;

/// <summary>Outcome of the provider turn offered leased tool-result detail.</summary>
public enum ToolResultConsumptionOutcome
{
    Success,
    ProviderFailure,
    ContentFiltered,
    Cancelled,
}

/// <summary>
/// One provider-request projection and the typed results whose detail it offered. The batch is
/// committed atomically only after a successful assistant continuation.
/// </summary>
public sealed record ToolResultContextProjection(
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<ToolResultContextLeaseKey> OfferedResults);

/// <summary>Stable identity of one revision leased into model context.</summary>
public sealed record ToolResultContextLeaseKey(ToolResultId ResultId, long Revision);

/// <summary>
/// Replaces already-consumed typed tool-result detail with compact receipts while leaving the
/// authoritative agent timeline untouched. Untyped and failed tool results retain their existing
/// behavior until generic stored-result envelope adoption supplies a safe recall contract.
/// </summary>
public sealed class ToolResultContextLease : IDisposable
{
    private readonly HashSet<ToolResultContextLeaseKey> _consumed = [];
    private readonly Dictionary<ToolResultContextLeaseKey, ActiveLease> _active = [];
    private bool _disposed;

    /// <summary>Builds the model-visible projection for one provider attempt.</summary>
    public ToolResultContextProjection Project(IReadOnlyList<AgentMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        List<AgentMessage>? projected = null;
        var offered = new List<ToolResultContextLeaseKey>();
        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index] is not ToolResultAgentMessage
                {
                    IsError: false,
                    Result.Details: ToolResultReceipt receipt,
                } toolResult)
            {
                continue;
            }

            var key = new ToolResultContextLeaseKey(receipt.ResultId, receipt.Revision);
            if (_consumed.Contains(key))
            {
                projected ??= messages.ToList();
                var receiptMessage = ToReceiptMessage(toolResult, receipt);
                projected[index] = receiptMessage;
                ToolResultContextTelemetry.RecordReceiptProjection(
                    receipt.Kind,
                    MeasureContentBytes(toolResult.Result.Content),
                    MeasureContentBytes(receiptMessage.Result.Content));
            }
            else
            {
                offered.Add(key);
                if (!_active.ContainsKey(key))
                {
                    var active = new ActiveLease(
                        receipt.Kind,
                        MeasureContentBytes(toolResult.Result.Content));
                    _active.Add(key, active);
                    ToolResultContextTelemetry.ChangeActiveLeasedBytes(active.Kind, active.Bytes);
                }
            }
        }

        return new ToolResultContextProjection(projected ?? messages, offered);
    }

    /// <summary>
    /// Commits every result offered in the request together, and only after a successful consuming
    /// assistant turn. Failure, cancellation, filtering, and retry paths are deliberate no-ops.
    /// </summary>
    public void Complete(ToolResultContextProjection projection, ToolResultConsumptionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (outcome != ToolResultConsumptionOutcome.Success)
        {
            return;
        }

        foreach (var key in projection.OfferedResults)
        {
            _consumed.Add(key);
            if (_active.Remove(key, out var active))
            {
                ToolResultContextTelemetry.ChangeActiveLeasedBytes(active.Kind, -active.Bytes);
            }
        }
    }

    /// <summary>Releases measurements for detail that was still leased when the run ended.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var active in _active.Values)
        {
            ToolResultContextTelemetry.ChangeActiveLeasedBytes(active.Kind, -active.Bytes);
        }

        _active.Clear();
        _disposed = true;
    }

    private static long MeasureContentBytes(IReadOnlyList<AgentToolContent> content) =>
        content.Sum(item => (long)Encoding.UTF8.GetByteCount(item.Value));

    private static ToolResultAgentMessage ToReceiptMessage(
        ToolResultAgentMessage message,
        ToolResultReceipt receipt)
    {
        var projection = receipt.ToModelProjection();
        var recall = "Retained tool-result detail has been consumed. Use a supported stored-result "
            + "operation with result_id and revision to recall bounded evidence; do not rerun the source tool.";
        return message with
        {
            Result = new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, $"{projection}\n{recall}")],
                receipt),
        };
    }

    private sealed record ActiveLease(ToolResultKind Kind, long Bytes);
}

/// <summary>Payload-free measurements for live tool-result context leasing.</summary>
public static class ToolResultContextTelemetry
{
    public const string MeterName = "BotNexus.Agent.ToolResultContext";
    public const string ActiveLeasedBytesInstrumentName = "botnexus.agent.tool_result.active_leased_bytes";
    public const string ReceiptBytesInstrumentName = "botnexus.agent.tool_result.receipt_bytes";
    public const string SavedPromptBytesInstrumentName = "botnexus.agent.tool_result.saved_prompt_bytes";

    private static readonly Meter Meter = new(MeterName);
    private static readonly UpDownCounter<long> ActiveLeasedBytes = Meter.CreateUpDownCounter<long>(
        ActiveLeasedBytesInstrumentName,
        "By",
        "Current detailed tool-result bytes leased into live model context.");
    private static readonly Counter<long> ReceiptBytes = Meter.CreateCounter<long>(
        ReceiptBytesInstrumentName,
        "By",
        "Compact receipt bytes projected into provider context.");
    private static readonly Counter<long> SavedPromptBytes = Meter.CreateCounter<long>(
        SavedPromptBytesInstrumentName,
        "By",
        "Detailed tool-result bytes omitted from provider context after consumption.");

    internal static void ChangeActiveLeasedBytes(ToolResultKind kind, long bytes) =>
        ActiveLeasedBytes.Add(bytes, KindTag(kind));

    internal static void RecordReceiptProjection(ToolResultKind kind, long detailedBytes, long receiptBytes)
    {
        var tags = KindTag(kind);
        ReceiptBytes.Add(receiptBytes, tags);
        SavedPromptBytes.Add(Math.Max(0, detailedBytes - receiptBytes), tags);
    }

    private static KeyValuePair<string, object?> KindTag(ToolResultKind kind) =>
        new("tool.result.kind", kind.ToString().ToLowerInvariant());
}

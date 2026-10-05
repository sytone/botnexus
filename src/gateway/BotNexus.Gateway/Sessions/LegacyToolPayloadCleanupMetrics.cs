using System.Diagnostics.Metrics;
using BotNexus.Gateway.Telemetry;

namespace BotNexus.Gateway.Sessions;

/// <summary>Payload-free measurements for committed legacy transcript cleanup batches.</summary>
internal sealed class LegacyToolPayloadCleanupMetrics
{
    internal static readonly string BatchesInstrumentName = BotNexusMeters.InstrumentName("session_cleanup", "batches");
    internal static readonly string CleanedInvocationsInstrumentName = BotNexusMeters.InstrumentName("session_cleanup", "cleaned_invocations");
    internal static readonly string CleanedRowsInstrumentName = BotNexusMeters.InstrumentName("session_cleanup", "cleaned_rows");
    internal static readonly string ClearedBytesInstrumentName = BotNexusMeters.InstrumentName("session_cleanup", "cleared_bytes");
    internal static readonly string ProtectedInvocationsInstrumentName = BotNexusMeters.InstrumentName("session_cleanup", "protected_invocations");
    internal static readonly string RemainingInvocationsInstrumentName = BotNexusMeters.InstrumentName("session_cleanup", "remaining_invocations");

    private readonly Counter<long>? _batches;
    private readonly Counter<long>? _cleanedInvocations;
    private readonly Counter<long>? _cleanedRows;
    private readonly Counter<long>? _clearedBytes;
    private readonly Counter<long>? _protectedInvocations;
    private readonly ObservableGauge<long>? _remainingInvocations;
    private long _remaining;

    internal LegacyToolPayloadCleanupMetrics(IMetrics? metrics)
    {
        if (metrics is null) return;
        _batches = metrics.CreateCounter<long>(BatchesInstrumentName, unit: "{batch}", description: "Legacy transcript cleanup batches by outcome.");
        _cleanedInvocations = metrics.CreateCounter<long>(CleanedInvocationsInstrumentName, unit: "{invocation}", description: "Invocations cleaned after normalized evidence parity.");
        _cleanedRows = metrics.CreateCounter<long>(CleanedRowsInstrumentName, unit: "{row}", description: "Legacy history rows cleaned.");
        _clearedBytes = metrics.CreateCounter<long>(ClearedBytesInstrumentName, unit: "By", description: "Logical UTF-8 payload bytes cleared.");
        _protectedInvocations = metrics.CreateCounter<long>(ProtectedInvocationsInstrumentName, unit: "{invocation}", description: "Protected invocations observed in committed cleanup batches.");
        _remainingInvocations = metrics.CreateObservableGauge<long>(RemainingInvocationsInstrumentName, () => Interlocked.Read(ref _remaining), unit: "{invocation}", description: "Invocations retaining legacy payload after the latest committed batch.");
    }

    internal void RecordCommitted(LegacyToolPayloadCleanupReport report)
    {
        _batches?.Add(1, new KeyValuePair<string, object?>("outcome", "committed"), new KeyValuePair<string, object?>("has_more", report.HasMore));
        _cleanedInvocations?.Add(report.CleanedInvocations);
        _cleanedRows?.Add(report.CleanedRows);
        _clearedBytes?.Add(report.ClearedBytes);
        _protectedInvocations?.Add(report.ProtectedInvocations);
        Interlocked.Exchange(ref _remaining, report.RemainingInvocations);
    }

    internal void RecordFailure() => _batches?.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
}

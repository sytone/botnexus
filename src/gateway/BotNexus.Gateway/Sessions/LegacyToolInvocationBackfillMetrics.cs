using System.Diagnostics.Metrics;
using BotNexus.Gateway.Telemetry;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Payload-free measurements for automatic legacy tool-invocation normalization.
/// </summary>
internal sealed class LegacyToolInvocationBackfillMetrics
{
    internal static readonly string BatchesInstrumentName =
        BotNexusMeters.InstrumentName("session_backfill", "batches");
    internal static readonly string ScannedRowsInstrumentName =
        BotNexusMeters.InstrumentName("session_backfill", "scanned_rows");
    internal static readonly string LinkedRowsInstrumentName =
        BotNexusMeters.InstrumentName("session_backfill", "linked_rows");
    internal static readonly string InvocationsInstrumentName =
        BotNexusMeters.InstrumentName("session_backfill", "invocations");

    private readonly Counter<long>? _batches;
    private readonly Counter<long>? _scannedRows;
    private readonly Counter<long>? _linkedRows;
    private readonly Counter<long>? _invocations;

    internal LegacyToolInvocationBackfillMetrics(IMetrics? metrics)
    {
        if (metrics is null)
            return;

        _batches = metrics.CreateCounter<long>(
            BatchesInstrumentName,
            unit: "{batch}",
            description: "Automatic legacy tool-invocation backfill batches by outcome.");
        _scannedRows = metrics.CreateCounter<long>(
            ScannedRowsInstrumentName,
            unit: "{row}",
            description: "Legacy session-history rows scanned by committed backfill batches.");
        _linkedRows = metrics.CreateCounter<long>(
            LinkedRowsInstrumentName,
            unit: "{row}",
            description: "Legacy session-history rows linked to normalized invocations.");
        _invocations = metrics.CreateCounter<long>(
            InvocationsInstrumentName,
            unit: "{invocation}",
            description: "Normalized tool invocations processed by automatic backfill.");
    }

    internal void RecordCommitted(LegacyToolInvocationBackfillReport report)
    {
        _batches?.Add(
            1,
            new KeyValuePair<string, object?>("outcome", "committed"),
            new KeyValuePair<string, object?>("has_more", report.HasMore));
        _scannedRows?.Add(report.ScannedRows);
        _linkedRows?.Add(report.LinkedRows);
        _invocations?.Add(report.InvocationCount);
    }

    internal void RecordFailure() =>
        _batches?.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
}

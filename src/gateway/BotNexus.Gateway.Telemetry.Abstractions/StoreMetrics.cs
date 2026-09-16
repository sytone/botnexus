using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BotNexus.Gateway.Telemetry;

/// <summary>
/// Records bounded-cardinality persistence operation latency and collection sizes on the
/// canonical BotNexus meter. Telemetry is best-effort: instrument creation and recording
/// failures never alter the store operation being observed.
/// </summary>
public sealed class StoreMetrics
{
    public static readonly string DurationInstrumentName = BotNexusMeters.InstrumentName("store", "duration");
    public static readonly string RowsInstrumentName = BotNexusMeters.InstrumentName("store", "rows");

    private readonly Histogram<double>? _duration;
    private readonly Histogram<long>? _rows;

    public StoreMetrics(IMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        try
        {
            _duration = metrics.CreateHistogram<double>(
                DurationInstrumentName,
                unit: "ms",
                description: "Store operation wall-clock duration in milliseconds.");
            _rows = metrics.CreateHistogram<long>(
                RowsInstrumentName,
                unit: "{row}",
                description: "Rows returned by collection-valued store read operations.");
        }
        catch
        {
            // Metrics are observational. A broken sink must not prevent store construction.
        }
    }

    public async Task<T> MeasureAsync<T>(
        string store,
        string operation,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            RecordDuration(store, operation, "success", started);
            return result;
        }
        catch
        {
            RecordDuration(store, operation, "failure", started);
            throw;
        }
    }

    public async Task<T> MeasureReadAsync<T>(
        string store,
        string operation,
        Func<CancellationToken, Task<T>> action,
        Func<T, int> rowCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rowCount);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            RecordRows(store, operation, rowCount(result));
            RecordDuration(store, operation, "success", started);
            return result;
        }
        catch
        {
            RecordDuration(store, operation, "failure", started);
            throw;
        }
    }

    public Operation Start(string store, string operation)
        => new(this, Bound(store), Bound(operation), Stopwatch.GetTimestamp());

    private void RecordDuration(string store, string operation, string outcome, long started)
    {
        try
        {
            _duration?.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new TagList
                {
                    { "store", Bound(store) },
                    { "operation", Bound(operation) },
                    { "outcome", outcome }
                });
        }
        catch
        {
            // Observability must not become part of store correctness.
        }
    }

    private void RecordRows(string store, string operation, int rows)
    {
        try
        {
            _rows?.Record(
                rows,
                new TagList
                {
                    { "store", Bound(store) },
                    { "operation", Bound(operation) },
                    { "outcome", "success" }
                });
        }
        catch
        {
            // Observability must not become part of store correctness.
        }
    }

    private static string Bound(string? value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value;

    public sealed class Operation : IDisposable
    {
        private readonly StoreMetrics _owner;
        private readonly string _store;
        private readonly string _operation;
        private readonly long _started;
        private int _completed;

        internal Operation(StoreMetrics owner, string store, string operation, long started)
        {
            _owner = owner;
            _store = store;
            _operation = operation;
            _started = started;
        }

        public void Complete(int? rows = null)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            if (rows.HasValue)
                _owner.RecordRows(_store, _operation, rows.Value);
            _owner.RecordDuration(_store, _operation, "success", _started);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
                _owner.RecordDuration(_store, _operation, "failure", _started);
        }
    }
}

namespace BotNexus.Agent.Providers.Copilot.Headers;

/// <summary>Process-local bounded latest observations; no persistent ledger or raw header storage.</summary>
public sealed class CopilotHeaderQuotaStore : ICopilotHeaderSink
{
    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly Dictionary<(CopilotHeaderScope Scope, CopilotQuotaDimension Dimension), CopilotHeaderObservation> _latest = new();
    private long _legacy;
    /// <summary>Creates a process-local store bounded by account scope and header dimension, not response history.</summary>
    public CopilotHeaderQuotaStore(int capacity = 256)
    {
        if (capacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }
    /// <summary>Number of retained account-scope and dimension snapshots, at most the configured capacity.</summary>
    public int Count { get { lock (_sync) return _latest.Count; } }
    /// <summary>Number of legacy responses observed without verified account attribution.</summary>
    public long LegacyUnattributedResponses => Interlocked.Read(ref _legacy);
    /// <inheritdoc />
    public void ObserveLegacyUnattributed() => Interlocked.Increment(ref _legacy);
    /// <summary>Retains newer logical requests, or later observations of the same request; older logical requests never win by finishing later.</summary>
    public void Observe(CopilotHeaderObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_sync)
        {
            var key = (observation.Scope, observation.Dimension);
            if (_latest.TryGetValue(key, out var previous) && (previous.RequestOrder > observation.RequestOrder ||
                (previous.RequestOrder == observation.RequestOrder && previous.ObservationOrder >= observation.ObservationOrder))) return;
            if (!_latest.ContainsKey(key) && _latest.Count >= _capacity)
            {
                var oldest = _latest.MinBy(pair => pair.Value.RequestOrder);
                if (oldest.Value.RequestOrder >= observation.RequestOrder) return;
                _latest.Remove(oldest.Key);
            }
            _latest[key] = observation;
        }
    }
    /// <summary>Returns only the latest typed snapshot for the exact scope and dimension, without guessing a default account.</summary>
    public CopilotHeaderObservation? GetLatest(CopilotHeaderScope scope, CopilotQuotaDimension dimension)
    {
        lock (_sync) return _latest.GetValueOrDefault((scope, dimension));
    }
}

using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Represents one bounded, capture-time-sanitised observation of an aggregated log pattern.
/// </summary>
public sealed class LogPatternOccurrence
{
    /// <summary>UTC instant at which the observation was captured.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Logger category that emitted the observation.</summary>
    public required string Category { get; init; }

    /// <summary>Numeric event identity supplied by the logger.</summary>
    public required int EventId { get; init; }

    /// <summary>Optional named event identity supplied by the logger.</summary>
    public string? EventName { get; init; }

    /// <summary>Sanitised and length-bounded rendered message.</summary>
    public required string RenderedMessage { get; init; }

    /// <summary>
    /// Allowlisted structured fields. An absent key means missing; a present key with a null value
    /// means the producer explicitly supplied null.
    /// </summary>
    public required IReadOnlyDictionary<string, string?> Properties { get; init; }
}

/// <summary>
/// Represents a deduplicated log pattern with bounded recent evidence and origin cardinalities.
/// </summary>
public sealed class LogPatternEntry
{
    private readonly object _gate = new();
    private readonly LinkedList<LogPatternOccurrence> _recentOccurrences = new();
    private readonly HashSet<string> _agentIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sessionIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _findingIds = new(StringComparer.Ordinal);
    private int _count = 1;
    private DateTimeOffset _lastSeen;

    /// <summary>Stable fingerprint derived from aggregate identity.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The bounded original message template.</summary>
    public required string Template { get; init; }

    /// <summary>Log severity level.</summary>
    public required LogLevel Severity { get; init; }

    /// <summary>Number of observations coalesced into the aggregate.</summary>
    public int Count { get { lock (_gate) return _count; } }

    /// <summary>Timestamp of the first observation.</summary>
    public required DateTimeOffset FirstSeen { get; init; }

    /// <summary>Timestamp of the most recent observation.</summary>
    public DateTimeOffset LastSeen { get { lock (_gate) return _lastSeen; } }

    /// <summary>Initialises the synchronized last-seen value during aggregate creation.</summary>
    internal DateTimeOffset InitialLastSeen { init => _lastSeen = value; }

    /// <summary>A sanitised sample message from the first observation.</summary>
    public required string SampleMessage { get; init; }

    /// <summary>Number of distinct retained agent identities observed for this pattern.</summary>
    public int DistinctAgentCount { get { lock (_gate) return _agentIds.Count; } }

    /// <summary>Number of distinct retained session identities observed for this pattern.</summary>
    public int DistinctSessionCount { get { lock (_gate) return _sessionIds.Count; } }

    /// <summary>Number of distinct retained finding identities observed for this pattern.</summary>
    public int DistinctFindingCount { get { lock (_gate) return _findingIds.Count; } }

    /// <summary>Recent sanitised occurrences ordered newest first.</summary>
    public IReadOnlyList<LogPatternOccurrence> RecentOccurrences
    {
        get { lock (_gate) return _recentOccurrences.ToArray(); }
    }

    internal void AddOccurrence(LogPatternOccurrence occurrence, int maxRecentOccurrences, int maxDistinctValues)
    {
        lock (_gate)
        {
            _count++;
            _lastSeen = occurrence.Timestamp;
            AddDistinct(_agentIds, occurrence.Properties, "AgentId", maxDistinctValues);
            AddDistinct(_sessionIds, occurrence.Properties, "SessionId", maxDistinctValues);
            AddDistinct(_findingIds, occurrence.Properties, "FindingId", maxDistinctValues);
            _recentOccurrences.AddFirst(occurrence);
            while (_recentOccurrences.Count > maxRecentOccurrences)
                _recentOccurrences.RemoveLast();
        }
    }

    internal void AddInitialOccurrence(LogPatternOccurrence occurrence, int maxDistinctValues)
    {
        lock (_gate)
        {
            AddDistinct(_agentIds, occurrence.Properties, "AgentId", maxDistinctValues);
            AddDistinct(_sessionIds, occurrence.Properties, "SessionId", maxDistinctValues);
            AddDistinct(_findingIds, occurrence.Properties, "FindingId", maxDistinctValues);
            _recentOccurrences.AddFirst(occurrence);
        }
    }

    private static void AddDistinct(
        HashSet<string> destination,
        IReadOnlyDictionary<string, string?> properties,
        string key,
        int maximum)
    {
        if (destination.Count < maximum && properties.TryGetValue(key, out var value) && value is not null)
            destination.Add(value);
    }
}

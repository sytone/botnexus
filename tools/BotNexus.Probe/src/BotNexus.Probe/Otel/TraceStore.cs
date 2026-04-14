namespace BotNexus.Probe.Otel;

public sealed class TraceStore(int capacity = 10_000)
{
    private readonly object _gate = new();
    private readonly LinkedList<SpanModel> _buffer = new();
    private readonly Dictionary<string, List<SpanModel>> _byTrace = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<SpanModel>> _bySession = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _capacity = Math.Max(1, capacity);

    public void AddSpans(IEnumerable<SpanModel> spans)
    {
        lock (_gate)
        {
            foreach (var span in spans)
            {
                _buffer.AddLast(span);
                AddToIndex(_byTrace, span.TraceId, span);
                if (TrySessionId(span.Attributes, out var sessionId))
                {
                    AddToIndex(_bySession, sessionId, span);
                }
            }

            while (_buffer.Count > _capacity)
            {
                var oldest = _buffer.First?.Value;
                _buffer.RemoveFirst();
                if (oldest is not null)
                {
                    RemoveFromIndex(_byTrace, oldest.TraceId, oldest);
                    if (TrySessionId(oldest.Attributes, out var sessionId))
                    {
                        RemoveFromIndex(_bySession, sessionId, oldest);
                    }
                }
            }
        }
    }

    public IReadOnlyList<SpanModel> GetTraces(int limit)
    {
        lock (_gate)
        {
            return _buffer.Reverse().Take(Math.Max(1, limit)).ToList();
        }
    }

    public IReadOnlyList<SpanModel> GetTraceById(string traceId)
    {
        lock (_gate)
        {
            return _byTrace.TryGetValue(traceId, out var spans)
                ? spans.OrderBy(span => span.StartTime).ToList()
                : [];
        }
    }

    public IReadOnlyList<SpanModel> SearchByAttribute(string key, string value)
    {
        lock (_gate)
        {
            return _buffer
                .Where(span => span.Attributes.TryGetValue(key, out var spanValue) &&
                    spanValue.Contains(value, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(span => span.StartTime)
                .ToList();
        }
    }

    public IReadOnlyList<SpanModel> SearchBySession(string sessionId)
    {
        lock (_gate)
        {
            return _bySession.TryGetValue(sessionId, out var spans)
                ? spans.OrderByDescending(span => span.StartTime).ToList()
                : [];
        }
    }

    private static bool TrySessionId(IReadOnlyDictionary<string, string> attributes, out string sessionId)
    {
        foreach (var key in new[] { "session.id", "sessionId", "session_id" })
        {
            if (attributes.TryGetValue(key, out sessionId!))
            {
                return true;
            }
        }

        sessionId = string.Empty;
        return false;
    }

    private static void AddToIndex(Dictionary<string, List<SpanModel>> index, string key, SpanModel span)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index[key] = list;
        }

        list.Add(span);
    }

    private static void RemoveFromIndex(Dictionary<string, List<SpanModel>> index, string key, SpanModel span)
    {
        if (!index.TryGetValue(key, out var list))
        {
            return;
        }

        list.Remove(span);
        if (list.Count == 0)
        {
            index.Remove(key);
        }
    }
}

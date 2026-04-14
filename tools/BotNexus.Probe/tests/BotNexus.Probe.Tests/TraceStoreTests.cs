using BotNexus.Probe.Otel;
using FluentAssertions;

namespace BotNexus.Probe.Tests;

public sealed class TraceStoreTests
{
    [Fact]
    public void AddSpans_MakesSpansRetrievable()
    {
        var store = new TraceStore();
        var span = CreateSpan("trace-1", "span-1", startSeconds: 1);

        store.AddSpans([span]);

        store.GetTraces(10).Should().ContainSingle().Which.Should().Be(span);
    }

    [Fact]
    public void AddSpans_WhenCapacityExceeded_EvictsOldest()
    {
        var store = new TraceStore(capacity: 2);
        var first = CreateSpan("trace-1", "s1", startSeconds: 1);
        var second = CreateSpan("trace-2", "s2", startSeconds: 2);
        var third = CreateSpan("trace-3", "s3", startSeconds: 3);

        store.AddSpans([first, second, third]);

        var traces = store.GetTraces(10);
        traces.Should().HaveCount(2);
        traces.Should().NotContain(first);
        traces.Should().Contain([second, third]);
    }

    [Fact]
    public void GetTraces_ReturnsMostRecentFirst()
    {
        var store = new TraceStore();
        var older = CreateSpan("trace-1", "older", startSeconds: 1);
        var newer = CreateSpan("trace-1", "newer", startSeconds: 2);

        store.AddSpans([older, newer]);

        store.GetTraces(2).Select(s => s.SpanId).Should().Equal("newer", "older");
    }

    [Fact]
    public void GetTraceById_ReturnsAllSpansForTraceOrderedByStartTime()
    {
        var store = new TraceStore();
        var first = CreateSpan("trace-1", "s1", startSeconds: 10);
        var second = CreateSpan("trace-1", "s2", startSeconds: 20);
        var third = CreateSpan("trace-2", "s3", startSeconds: 30);
        store.AddSpans([second, third, first]);

        var trace = store.GetTraceById("trace-1");

        trace.Select(s => s.SpanId).Should().Equal("s1", "s2");
    }

    [Fact]
    public void SearchByAttribute_FindsMatchingSpans()
    {
        var store = new TraceStore();
        store.AddSpans(
        [
            CreateSpan("trace-1", "s1", 1, ("http.route", "/health")),
            CreateSpan("trace-2", "s2", 2, ("http.route", "/v1/traces")),
            CreateSpan("trace-3", "s3", 3, ("db.statement", "select"))
        ]);

        var matches = store.SearchByAttribute("http.route", "traces");

        matches.Should().ContainSingle();
        matches[0].SpanId.Should().Be("s2");
    }

    [Fact]
    public void AddSpans_IsThreadSafeForConcurrentAdds()
    {
        var store = new TraceStore(capacity: 10_000);

        Parallel.For(0, 500, index =>
        {
            store.AddSpans([CreateSpan($"trace-{index % 10}", $"span-{index}", index, ("session.id", "sess-1"))]);
        });

        var spans = store.GetTraces(10_000);
        spans.Should().HaveCount(500);
        store.SearchBySession("sess-1").Should().HaveCount(500);
    }

    [Fact]
    public void EmptyStore_ReturnsEmptyResults()
    {
        var store = new TraceStore();

        store.GetTraces(100).Should().BeEmpty();
        store.GetTraceById("missing").Should().BeEmpty();
        store.SearchByAttribute("k", "v").Should().BeEmpty();
        store.SearchBySession("s").Should().BeEmpty();
    }

    private static SpanModel CreateSpan(
        string traceId,
        string spanId,
        int startSeconds,
        params (string key, string value)[] attributes)
    {
        var attrs = attributes.ToDictionary(a => a.key, a => a.value, StringComparer.OrdinalIgnoreCase);
        return new SpanModel(
            traceId,
            spanId,
            ParentSpanId: null,
            ServiceName: "svc",
            OperationName: "op",
            StartTime: DateTimeOffset.UnixEpoch.AddSeconds(startSeconds),
            Duration: TimeSpan.FromMilliseconds(10),
            Status: "Ok",
            Attributes: attrs);
    }
}

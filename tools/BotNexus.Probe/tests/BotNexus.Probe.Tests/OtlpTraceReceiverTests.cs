using BotNexus.Probe.Otel;
using FluentAssertions;
using System.Reflection;
using System.Text.Json;

namespace BotNexus.Probe.Tests;

public sealed class OtlpTraceReceiverTests
{
    [Fact]
    public void ParseSpans_ValidPayload_MapsSpanModels()
    {
        using var document = JsonDocument.Parse("""
{
  "resourceSpans": [
    {
      "resource": {
        "attributes": [
          { "key": "service.name", "value": { "stringValue": "probe-service" } }
        ]
      },
      "scopeSpans": [
        {
          "spans": [
            {
              "traceId": "trace-1",
              "spanId": "span-1",
              "parentSpanId": "root-0",
              "name": "GET /healthz",
              "startTimeUnixNano": "2000000000",
              "endTimeUnixNano": "3000000000",
              "status": { "code": "STATUS_CODE_OK" },
              "attributes": [
                { "key": "session.id", "value": { "stringValue": "sess-1" } },
                { "key": "http.status_code", "value": { "intValue": 200 } }
              ]
            }
          ]
        }
      ]
    }
  ]
}
""");

        var spans = ParseSpans(document.RootElement).ToList();

        spans.Should().ContainSingle();
        var span = spans[0];
        span.TraceId.Should().Be("trace-1");
        span.SpanId.Should().Be("span-1");
        span.ParentSpanId.Should().Be("root-0");
        span.ServiceName.Should().Be("probe-service");
        span.OperationName.Should().Be("GET /healthz");
        span.Status.Should().Be("Ok");
        span.Attributes["session.id"].Should().Be("sess-1");
        span.Attributes["http.status_code"].Should().Be("200");
        span.Duration.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ParseSpans_EmptyOrMalformedPayload_ReturnsNoSpans()
    {
        using var emptyDoc = JsonDocument.Parse("{}");
        using var malformedShape = JsonDocument.Parse("""{ "resourceSpans": { "bad": true } }""");

        ParseSpans(emptyDoc.RootElement).Should().BeEmpty();
        ParseSpans(malformedShape.RootElement).Should().BeEmpty();
    }

    [Fact]
    public void ParsedSpans_CanBeAddedToTraceStore()
    {
        var store = new TraceStore();

        using var document = JsonDocument.Parse("""
{
  "resourceSpans": [
    {
      "scopeSpans": [
        {
          "spans": [
            { "traceId": "t1", "spanId": "s1", "name": "op", "startTimeUnixNano": "100", "endTimeUnixNano": "200" }
          ]
        }
      ]
    }
  ]
}
""");

        store.AddSpans(ParseSpans(document.RootElement));

        store.GetTraces(10).Should().ContainSingle();
    }

    private static IEnumerable<SpanModel> ParseSpans(JsonElement root)
    {
        var method = typeof(OtlpTraceReceiver).GetMethod("ParseSpans", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (IEnumerable<SpanModel>)method.Invoke(null, [root])!;
    }
}

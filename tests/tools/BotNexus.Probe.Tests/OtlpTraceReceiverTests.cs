using BotNexus.Probe.Otel;
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

        spans.ShouldHaveSingleItem();
        var span = spans[0];
        span.TraceId.ShouldBe("trace-1");
        span.SpanId.ShouldBe("span-1");
        span.ParentSpanId.ShouldBe("root-0");
        span.ServiceName.ShouldBe("probe-service");
        span.OperationName.ShouldBe("GET /healthz");
        span.Status.ShouldBe("Ok");
        span.Attributes["session.id"].ShouldBe("sess-1");
        span.Attributes["http.status_code"].ShouldBe("200");
        span.Duration.ShouldBe(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ParseSpans_EmptyOrMalformedPayload_ReturnsNoSpans()
    {
        using var emptyDoc = JsonDocument.Parse("{}");
        using var malformedShape = JsonDocument.Parse("""{ "resourceSpans": { "bad": true } }""");

        ParseSpans(emptyDoc.RootElement).ShouldBeEmpty();
        ParseSpans(malformedShape.RootElement).ShouldBeEmpty();
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

        store.GetTraces(10).ShouldHaveSingleItem();
    }

    private static IEnumerable<SpanModel> ParseSpans(JsonElement root)
    {
        var method = typeof(OtlpTraceReceiver).GetMethod("ParseSpans", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (IEnumerable<SpanModel>)method.Invoke(null, [root])!;
    }
}

using System.Text.Json;

namespace BotNexus.Probe.Otel;

public sealed class OtlpTraceReceiver(TraceStore traceStore)
{
    public void MapEndpoint(WebApplication app)
    {
        app.MapPost("/v1/traces", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
                var spans = ParseSpans(document.RootElement).ToList();
                traceStore.AddSpans(spans);
                return Results.Ok(new { received = spans.Count });
            }
            catch (JsonException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });
    }

    private static IEnumerable<SpanModel> ParseSpans(JsonElement root)
    {
        if (!root.TryGetProperty("resourceSpans", out var resourceSpans) || resourceSpans.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var resourceSpan in resourceSpans.EnumerateArray())
        {
            var serviceName = ExtractServiceName(resourceSpan);
            if (!resourceSpan.TryGetProperty("scopeSpans", out var scopeSpans) || scopeSpans.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var scopeSpan in scopeSpans.EnumerateArray())
            {
                if (!scopeSpan.TryGetProperty("spans", out var spansElement) || spansElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var spanElement in spansElement.EnumerateArray())
                {
                    var attributes = ExtractAttributes(spanElement);
                    var operationName = TryGetString(spanElement, "name") ?? "unknown";
                    var traceId = TryGetString(spanElement, "traceId") ?? string.Empty;
                    var spanId = TryGetString(spanElement, "spanId") ?? string.Empty;
                    var parentSpanId = TryGetString(spanElement, "parentSpanId");
                    var start = FromUnixNano(TryGetString(spanElement, "startTimeUnixNano"));
                    var end = FromUnixNano(TryGetString(spanElement, "endTimeUnixNano"));
                    var duration = end > start ? end - start : TimeSpan.Zero;
                    var status = ParseStatus(spanElement);

                    yield return new SpanModel(
                        traceId,
                        spanId,
                        parentSpanId,
                        serviceName,
                        operationName,
                        start,
                        duration,
                        status,
                        attributes);
                }
            }
        }
    }

    private static string ExtractServiceName(JsonElement resourceSpan)
    {
        if (!resourceSpan.TryGetProperty("resource", out var resource))
        {
            return "unknown-service";
        }

        var resourceAttributes = ExtractAttributes(resource);
        return resourceAttributes.TryGetValue("service.name", out var serviceName)
            ? serviceName
            : "unknown-service";
    }

    private static Dictionary<string, string> ExtractAttributes(JsonElement element)
    {
        if (!element.TryGetProperty("attributes", out var attributesElement) || attributesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in attributesElement.EnumerateArray())
        {
            var key = TryGetString(attribute, "key");
            if (string.IsNullOrWhiteSpace(key) || !attribute.TryGetProperty("value", out var valueElement))
            {
                continue;
            }

            attributes[key] = ParseAnyValue(valueElement);
        }

        return attributes;
    }

    private static string ParseStatus(JsonElement span)
    {
        if (!span.TryGetProperty("status", out var status))
        {
            return "Unset";
        }

        var code = TryGetString(status, "code");
        return code?.ToUpperInvariant() switch
        {
            "STATUS_CODE_OK" or "OK" or "1" => "Ok",
            "STATUS_CODE_ERROR" or "ERROR" or "2" => "Error",
            _ => "Unset"
        };
    }

    private static DateTimeOffset FromUnixNano(string? raw)
    {
        if (!long.TryParse(raw, out var nanos) || nanos <= 0)
        {
            return DateTimeOffset.UtcNow;
        }

        var ticks = nanos / 100;
        return new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
    }

    private static string ParseAnyValue(JsonElement valueElement)
    {
        foreach (var property in valueElement.EnumerateObject())
        {
            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => property.Value.GetRawText()
            };
        }

        return string.Empty;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }
}

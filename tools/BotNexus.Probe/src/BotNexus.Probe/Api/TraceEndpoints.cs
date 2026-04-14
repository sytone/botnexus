using BotNexus.Probe.Otel;

namespace BotNexus.Probe.Api;

public static class TraceEndpoints
{
    public static IEndpointRouteBuilder MapTraceEndpoints(this IEndpointRouteBuilder app, TraceStore traceStore, bool isEnabled)
    {
        app.MapGet("/api/traces", (int? limit) =>
        {
            if (!isEnabled)
            {
                return Results.Problem("OTLP receiver is disabled.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(traceStore.GetTraces(Math.Clamp(limit ?? 100, 1, 1_000)));
        });

        app.MapGet("/api/traces/{traceId}", (string traceId) =>
        {
            if (!isEnabled)
            {
                return Results.Problem("OTLP receiver is disabled.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var spans = traceStore.GetTraceById(traceId);
            return spans.Count == 0
                ? Results.NotFound(new { error = $"Trace '{traceId}' not found." })
                : Results.Ok(spans);
        });

        app.MapGet("/api/traces/search", (string attr, string value, int? limit) =>
        {
            if (!isEnabled)
            {
                return Results.Problem("OTLP receiver is disabled.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var spans = traceStore.SearchByAttribute(attr, value).Take(Math.Clamp(limit ?? 100, 1, 1_000));
            return Results.Ok(spans);
        });

        return app;
    }
}

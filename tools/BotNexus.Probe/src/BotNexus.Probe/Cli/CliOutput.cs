using BotNexus.Probe.LogIngestion;
using BotNexus.Probe.Otel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Probe.Cli;

public static class CliOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Write(CliOptions options, object payload, Func<string> textFactory)
    {
        if (options.TextOutput)
        {
            Console.Out.WriteLine(textFactory());
            return;
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }

    public static void WriteError(string message)
        => Console.Error.WriteLine(message);

    public static JsonElement? ParseJsonElementOrNull(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    public static int DetectCount(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
        {
            return payload.GetArrayLength();
        }

        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("items", out var items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            return items.GetArrayLength();
        }

        return 1;
    }

    public static string FormatLogs(IReadOnlyList<LogEntry> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"📄 Log Entries ({items.Count} results)");
        builder.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━");

        foreach (var item in items)
        {
            builder.AppendLine($"[{item.Timestamp.ToLocalTime():HH:mm:ss} {item.Level}] {item.Message}");
            var context = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.SessionId))
            {
                context.Add($"Session: {item.SessionId}");
            }

            if (!string.IsNullOrWhiteSpace(item.CorrelationId))
            {
                context.Add($"Correlation: {item.CorrelationId}");
            }

            if (!string.IsNullOrWhiteSpace(item.AgentId))
            {
                context.Add($"Agent: {item.AgentId}");
            }

            if (context.Count > 0)
            {
                builder.AppendLine($"  {string.Join(" | ", context)}");
            }

            if (!string.IsNullOrWhiteSpace(item.Exception))
            {
                builder.AppendLine($"  {item.Exception}");
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public static string FormatSessionMessages(string sessionId, IReadOnlyList<SessionMessage> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"💬 Session {sessionId} ({items.Count} messages)");
        builder.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

        foreach (var item in items)
        {
            var icon = item.Role?.ToLowerInvariant() switch
            {
                "user" => "👤",
                "assistant" => "🤖",
                "tool" => "🔧",
                "system" => "⚙️",
                _ => "•"
            };

            var timestamp = item.Timestamp?.ToLocalTime().ToString("HH:mm:ss") ?? "--:--:--";
            var role = item.Role ?? "unknown";
            builder.AppendLine($"[{timestamp}] {icon} {role}: {item.Content}");
        }

        return builder.ToString().TrimEnd();
    }

    public static string FormatSpans(string title, IReadOnlyList<SpanModel> spans)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"🧵 {title} ({spans.Count} spans)");
        builder.AppendLine("━━━━━━━━━━━━━━━━━━━━━━");

        foreach (var span in spans)
        {
            builder.AppendLine($"[{span.StartTime.ToLocalTime():HH:mm:ss}] {span.ServiceName} :: {span.OperationName}");
            builder.AppendLine($"  Trace: {span.TraceId} | Span: {span.SpanId} | Status: {span.Status}");
        }

        return builder.ToString().TrimEnd();
    }
}

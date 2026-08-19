using System.Net;
using System.Text;
using BotNexus.Domain.Text;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Api.Export;

/// <summary>
/// Renders an <see cref="ExportDocument"/> as a single self-contained HTML document (issue #3278).
/// </summary>
/// <remarks>
/// <para>
/// The output is deliberately inert and offline: all styling is one inline <c>&lt;style&gt;</c>
/// block, there is no <c>&lt;script&gt;</c> element, and no element references a remote asset (no
/// <c>src</c>, no stylesheet <c>link</c>, no web font, no tracking pixel). A transcript is untrusted
/// user and model output; emitting it into a document that can execute script or phone home would
/// turn "download my conversation" into a stored-XSS and beaconing vector. Acceptance criterion 3
/// pins this with a test that parses the produced document rather than string-matching it.
/// </para>
/// <para>
/// Every value interpolated into the document goes through <see cref="WebUtility.HtmlEncode(string?)"/>, so
/// markup inside a message renders as literal text instead of becoming part of the document.
/// Redaction reuses <see cref="TranscriptSecretRedactor"/> exactly as the markdown renderer does.
/// </para>
/// </remarks>
public static class ExportHtmlRenderer
{
    private const int ToolResultMaxLength = 2000;

    private const string Style = """
        :root { color-scheme: light dark; }
        body { font-family: system-ui, -apple-system, "Segoe UI", sans-serif; margin: 0 auto;
               max-width: 52rem; padding: 2rem 1rem; line-height: 1.5; }
        header { border-bottom: 1px solid #8884; padding-bottom: 1rem; margin-bottom: 1.5rem; }
        dl.meta { display: grid; grid-template-columns: max-content 1fr; gap: 0.25rem 1rem; margin: 0; }
        dl.meta dt { font-weight: 600; }
        dl.meta dd { margin: 0; }
        article { border: 1px solid #8884; border-radius: 6px; padding: 0.75rem 1rem; margin: 0.75rem 0; }
        article.user { border-left: 4px solid #3b82f6; }
        article.assistant { border-left: 4px solid #10b981; }
        article.tool { border-left: 4px solid #f59e0b; }
        article.system { border-left: 4px solid #a855f7; }
        article.error { border-left: 4px solid #ef4444; }
        .role { font-weight: 600; }
        .time { color: #8889; font-size: 0.85em; margin-left: 0.5rem; }
        hr.boundary { border: 0; border-top: 2px dashed #8886; margin: 2rem 0 1rem; }
        pre { background: #8881; padding: 0.6rem; border-radius: 4px; overflow-x: auto; white-space: pre-wrap;
              word-break: break-word; }
        .content { white-space: pre-wrap; word-break: break-word; }
        .empty { color: #8889; font-style: italic; }
        """;

    /// <summary>
    /// Renders the document as a standalone HTML file.
    /// </summary>
    /// <param name="document">The assembled export document.</param>
    /// <param name="redactSecrets">When true, applies <see cref="TranscriptSecretRedactor"/> to all rendered text.</param>
    /// <returns>A complete HTML document. Always non-null: an empty conversation still renders its header.</returns>
    public static string Render(ExportDocument document, bool redactSecrets = true)
    {
        ArgumentNullException.ThrowIfNull(document);

        var heading = document.Scope == ExportScope.Conversation ? "Conversation Transcript" : "Session Transcript";
        var docTitle = string.IsNullOrWhiteSpace(document.Title)
            ? heading
            : $"{heading} — {Scrub(document.Title, redactSecrets)}";

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine($"<title>{Encode(docTitle)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(Style);
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        AppendHeader(sb, document, heading, redactSecrets);

        if (document.Entries.Count == 0)
        {
            sb.AppendLine("<p class=\"empty\">This conversation has no messages.</p>");
        }
        else
        {
            foreach (var entry in document.Entries)
                AppendEntry(sb, entry, redactSecrets);
        }

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, ExportDocument document, string heading, bool redact)
    {
        sb.AppendLine("<header>");
        sb.AppendLine($"<h1>{Encode(heading)}</h1>");
        sb.AppendLine("<dl class=\"meta\">");

        AppendMeta(sb, "Title", Scrub(document.Title, redact));
        AppendMeta(sb, "Conversation ID", document.ConversationId);
        AppendMeta(sb, "Agent", document.AgentId);
        AppendMeta(sb, "Purpose", Scrub(document.Purpose, redact));
        AppendMeta(sb, "Status", document.Status);
        if (document.CreatedAt is { } created)
            AppendMeta(sb, "Created", $"{created:yyyy-MM-dd HH:mm:ss} UTC");
        if (document.UpdatedAt is { } updated)
            AppendMeta(sb, "Updated", $"{updated:yyyy-MM-dd HH:mm:ss} UTC");
        AppendMeta(sb, "Model override", document.ModelOverride);
        AppendMeta(sb, "Thinking override", document.ThinkingOverride);
        if (document.ContextWindowOverride is { } context)
            AppendMeta(sb, "Context window override", context.ToString());
        AppendMeta(sb, "Messages", document.MessageCount.ToString());
        AppendMeta(sb, "Tool calls", document.ToolCallCount.ToString());
        AppendMeta(sb, "Exported", $"{document.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");

        sb.AppendLine("</dl>");

        if (!string.IsNullOrWhiteSpace(document.Instructions))
        {
            sb.AppendLine("<h2>Instructions</h2>");
            sb.AppendLine($"<div class=\"content\">{Encode(Scrub(document.Instructions, redact))}</div>");
        }

        if (document.Sessions.Count > 0)
        {
            sb.AppendLine("<h2>Sessions</h2>");
            sb.AppendLine("<ul>");
            foreach (var session in document.Sessions)
            {
                var text = $"{session.SessionId} — {session.Status}, {session.MessageCount} message(s), " +
                           $"started {session.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC";
                sb.AppendLine($"<li>{Encode(text)}</li>");
            }
            sb.AppendLine("</ul>");
        }

        sb.AppendLine("</header>");
    }

    private static void AppendMeta(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        sb.AppendLine($"<dt>{Encode(label)}</dt><dd>{Encode(value)}</dd>");
    }

    private static void AppendEntry(StringBuilder sb, ConversationHistoryEntry entry, bool redact)
    {
        var time = Encode(entry.Timestamp.ToString("HH:mm:ss"));

        if (entry.Kind == "boundary")
        {
            sb.AppendLine("<hr class=\"boundary\">");
            sb.AppendLine(
                $"<p class=\"role\">Session boundary — {Encode(entry.SessionId)} ended" +
                $"<span class=\"time\">{time}</span></p>");
            return;
        }

        if (entry.Kind == "compaction")
        {
            sb.AppendLine("<article class=\"system\">");
            sb.AppendLine($"<p class=\"role\">Compaction summary<span class=\"time\">{time}</span></p>");
            sb.AppendLine($"<div class=\"content\">{Encode(Scrub(entry.Content, redact))}</div>");
            sb.AppendLine("</article>");
            return;
        }

        var content = Scrub(entry.Content, redact) ?? string.Empty;

        switch (entry.Role)
        {
            case "user":
                AppendSimple(sb, "user", "User", time, content);
                break;

            case "assistant":
                AppendSimple(sb, "assistant", "Assistant", time, content);
                break;

            case "tool":
                if (!string.IsNullOrEmpty(entry.ToolName) && !string.IsNullOrEmpty(entry.ToolArgs))
                {
                    sb.AppendLine("<article class=\"tool\">");
                    sb.AppendLine(
                        $"<p class=\"role\">Tool Call: {Encode(entry.ToolName)}<span class=\"time\">{time}</span></p>");
                    sb.AppendLine($"<pre>{Encode(Scrub(entry.ToolArgs, redact))}</pre>");
                    sb.AppendLine("</article>");
                }
                else
                {
                    var cls = entry.ToolIsError ? "error" : "tool";
                    var label = entry.ToolIsError ? "Tool Error" : "Tool Result";
                    if (!string.IsNullOrEmpty(entry.ToolName))
                        label = $"{label}: {entry.ToolName}";
                    sb.AppendLine($"<article class=\"{cls}\">");
                    sb.AppendLine($"<p class=\"role\">{Encode(label)}<span class=\"time\">{time}</span></p>");
                    sb.AppendLine($"<pre>{Encode(Truncate(content))}</pre>");
                    sb.AppendLine("</article>");
                }
                break;

            case "system":
                AppendSimple(sb, "system", "System", time, content);
                break;

            case "notification":
                AppendSimple(sb, "system", "Notification", time, content);
                break;
        }
    }

    private static void AppendSimple(StringBuilder sb, string cls, string label, string time, string content)
    {
        sb.AppendLine($"<article class=\"{cls}\">");
        sb.AppendLine($"<p class=\"role\">{Encode(label)}<span class=\"time\">{time}</span></p>");
        sb.AppendLine($"<div class=\"content\">{Encode(content)}</div>");
        sb.AppendLine("</article>");
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string? Scrub(string? value, bool redact)
        => redact ? TranscriptSecretRedactor.Redact(value) : value;

    private static string Truncate(string value)
        => TextTruncation.SafeTruncate(value, ToolResultMaxLength, "\n... (truncated)")!;
}

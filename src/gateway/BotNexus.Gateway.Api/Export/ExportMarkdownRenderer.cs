using System.Text;
using BotNexus.Domain.Text;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Api.Export;

/// <summary>
/// Renders an <see cref="ExportDocument"/> as a markdown document, following the heading, emoji and
/// fenced-block conventions established by <see cref="SessionTranscriptRenderer"/> (issue #3278).
/// </summary>
/// <remarks>
/// Secret redaction is applied here, at render time, through the existing
/// <see cref="TranscriptSecretRedactor"/> policy rather than being reimplemented - the same
/// arrangement <see cref="SessionTranscriptRenderer"/> uses. Redaction covers message content, tool
/// arguments, tool results, conversation instructions and the purpose/title header fields, because a
/// credential pasted into a conversation's instructions leaves the process just as readily as one
/// pasted into a message.
/// </remarks>
public static class ExportMarkdownRenderer
{
    /// <summary>Maximum rendered length of a tool result before truncation, matching the session renderer.</summary>
    private const int ToolResultMaxLength = 2000;

    /// <summary>
    /// Renders the document as markdown.
    /// </summary>
    /// <param name="document">The assembled export document.</param>
    /// <param name="redactSecrets">When true, applies <see cref="TranscriptSecretRedactor"/> to all rendered text.</param>
    /// <returns>The markdown document. Always non-null: an empty conversation still renders its header.</returns>
    public static string Render(ExportDocument document, bool redactSecrets = true)
    {
        ArgumentNullException.ThrowIfNull(document);

        var sb = new StringBuilder();
        var heading = ExportHeading.For(document.Scope);
        sb.AppendLine($"# {heading}");
        sb.AppendLine();

        AppendHeader(sb, document, redactSecrets);

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        if (document.Entries.Count == 0)
        {
            sb.AppendLine("_This conversation has no messages._");
            return sb.ToString();
        }

        foreach (var entry in document.Entries)
            AppendEntry(sb, entry, redactSecrets);

        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, ExportDocument document, bool redact)
    {
        if (!string.IsNullOrWhiteSpace(document.Title))
            sb.AppendLine($"- **Title:** {Scrub(document.Title, redact)}");
        if (!string.IsNullOrWhiteSpace(document.ConversationId))
            sb.AppendLine($"- **Conversation ID:** `{document.ConversationId}`");
        if (!string.IsNullOrWhiteSpace(document.AgentId))
            sb.AppendLine($"- **Agent:** `{document.AgentId}`");
        if (!string.IsNullOrWhiteSpace(document.Purpose))
            sb.AppendLine($"- **Purpose:** {Scrub(document.Purpose, redact)}");
        if (!string.IsNullOrWhiteSpace(document.Status))
            sb.AppendLine($"- **Status:** {document.Status}");
        if (document.CreatedAt is { } created)
            sb.AppendLine($"- **Created:** {created:yyyy-MM-dd HH:mm:ss} UTC");
        if (document.UpdatedAt is { } updated)
            sb.AppendLine($"- **Updated:** {updated:yyyy-MM-dd HH:mm:ss} UTC");
        if (!string.IsNullOrWhiteSpace(document.ModelOverride))
            sb.AppendLine($"- **Model override:** `{document.ModelOverride}`");
        if (!string.IsNullOrWhiteSpace(document.ThinkingOverride))
            sb.AppendLine($"- **Thinking override:** `{document.ThinkingOverride}`");
        if (document.ContextWindowOverride is { } context)
            sb.AppendLine($"- **Context window override:** {context}");

        sb.AppendLine($"- **Messages:** {document.MessageCount}");
        sb.AppendLine($"- **Tool calls:** {document.ToolCallCount}");
        sb.AppendLine($"- **Exported:** {document.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");

        // #3279 AC3: the omission note is rendered immediately after the totals, not buried at the
        // foot of the file, because the totals directly above it are range totals and a reader must
        // learn that before drawing any conclusion from them.
        if (document.Range is { } range)
        {
            sb.AppendLine($"- **Scope:** excerpt");
            sb.AppendLine($"- **Range:** `{range.FirstEntryId}` to `{range.LastEntryId}`");
            sb.AppendLine($"- **Entries omitted:** {document.OmittedEntryCount}");
            sb.AppendLine();
            sb.AppendLine($"> **Note:** {document.OmissionNote}");
        }

        if (!string.IsNullOrWhiteSpace(document.Instructions))
        {
            sb.AppendLine();
            sb.AppendLine("## Instructions");
            sb.AppendLine();
            sb.AppendLine(Scrub(document.Instructions, redact));
        }

        if (document.Sessions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Sessions");
            sb.AppendLine();
            foreach (var session in document.Sessions)
            {
                sb.AppendLine(
                    $"- `{session.SessionId}` — {session.Status}, {session.MessageCount} message(s), " +
                    $"started {session.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC");
            }
        }
    }

    private static void AppendEntry(StringBuilder sb, ConversationHistoryEntry entry, bool redact)
    {
        var timestamp = entry.Timestamp.ToString("HH:mm:ss");

        if (entry.Kind == "boundary")
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"## 🔚 Session boundary — `{entry.SessionId}` ended [{timestamp}]");
            sb.AppendLine();
            return;
        }

        if (entry.Kind == "compaction")
        {
            sb.AppendLine($"## 🗜️ Compaction summary [{timestamp}]");
            sb.AppendLine();
            sb.AppendLine($"_{Scrub(entry.Content, redact)}_");
            sb.AppendLine();
            return;
        }

        var content = Scrub(entry.Content, redact) ?? string.Empty;

        switch (entry.Role)
        {
            case "user":
                sb.AppendLine($"## 🧑 User [{timestamp}]");
                sb.AppendLine();
                foreach (var line in content.Split('\n'))
                {
                    sb.Append("> ");
                    sb.AppendLine(line);
                }
                sb.AppendLine();
                break;

            case "assistant":
                sb.AppendLine($"## 🤖 Assistant [{timestamp}]");
                sb.AppendLine();
                sb.AppendLine(content);
                sb.AppendLine();
                break;

            case "tool":
                if (!string.IsNullOrEmpty(entry.ToolName) && !string.IsNullOrEmpty(entry.ToolArgs))
                {
                    sb.AppendLine($"### 🔧 Tool Call: `{entry.ToolName}` [{timestamp}]");
                    sb.AppendLine();
                    sb.AppendLine("```json");
                    sb.AppendLine(Scrub(entry.ToolArgs, redact));
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
                else
                {
                    var label = entry.ToolIsError ? "Tool Error" : "Tool Result";
                    var toolLabel = !string.IsNullOrEmpty(entry.ToolName) ? $": `{entry.ToolName}`" : "";
                    sb.AppendLine($"### 📋 {label}{toolLabel} [{timestamp}]");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(Truncate(content));
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
                break;

            case "system":
                sb.AppendLine($"### ⚙️ System [{timestamp}]");
                sb.AppendLine();
                sb.AppendLine($"_{content}_");
                sb.AppendLine();
                break;

            case "notification":
                sb.AppendLine($"> **ℹ️ Notification [{timestamp}]:** {content}");
                sb.AppendLine();
                break;
        }
    }

    private static string? Scrub(string? value, bool redact)
        => redact ? TranscriptSecretRedactor.Redact(value) : value;

    private static string Truncate(string value)
        => TextTruncation.SafeTruncate(value, ToolResultMaxLength, "\n... (truncated)")!;
}

using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Renders a session's history entries as a human-readable markdown transcript.
/// </summary>
public static class SessionTranscriptRenderer
{
    /// <summary>
    /// Renders the session history as a markdown document.
    /// </summary>
    /// <param name="session">The session to render.</param>
    /// <param name="agentId">The agent ID that owns this session.</param>
    /// <param name="redactSecrets">
    /// When true, applies <see cref="TranscriptSecretRedactor"/> to entry content, tool
    /// arguments, and tool results so recognised credential shapes are replaced
    /// before the transcript leaves the process (e.g. via the export API).
    /// Defaults to false so rendering stays byte-identical to the un-redacted
    /// behaviour unless a caller explicitly opts in.
    /// </param>
    /// <returns>A markdown string, or null if the session has no entries.</returns>
    public static string? RenderMarkdown(GatewaySession session, string? agentId = null, bool redactSecrets = false)
    {
        ArgumentNullException.ThrowIfNull(session);

        var entries = session.History;
        if (entries.Count == 0)
            return null;

        var sb = new StringBuilder();

        // Header
        sb.AppendLine("# Session Transcript");
        sb.AppendLine();
        sb.AppendLine($"- **Session ID:** `{session.SessionId.Value}`");
        if (!string.IsNullOrWhiteSpace(agentId))
            sb.AppendLine($"- **Agent:** `{agentId}`");
        if (!string.IsNullOrWhiteSpace(session.ConversationId.Value))
            sb.AppendLine($"- **Conversation:** `{session.ConversationId.Value}`");
        sb.AppendLine($"- **Started:** {session.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"- **Status:** {session.Status}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var entry in entries)
        {
            if (entry.IsCrashSentinel)
                continue;

            var timestamp = entry.Timestamp.ToString("HH:mm:ss");
            var content = Scrub(entry.Content, redactSecrets);

            if (entry.Role == MessageRole.User)
            {
                sb.AppendLine($"## 🧑 User [{timestamp}]");
                sb.AppendLine();
                foreach (var line in content.Split('\n'))
                {
                    sb.Append("> ");
                    sb.AppendLine(line);
                }
                sb.AppendLine();
            }
            else if (entry.Role == MessageRole.Assistant)
            {
                sb.AppendLine($"## 🤖 Assistant [{timestamp}]");
                sb.AppendLine();
                sb.AppendLine(content);
                sb.AppendLine();
            }
            else if (entry.Role == MessageRole.Tool)
            {
                if (!string.IsNullOrEmpty(entry.ToolName) && !string.IsNullOrEmpty(entry.ToolArgs))
                {
                    sb.AppendLine($"### 🔧 Tool Call: `{entry.ToolName}` [{timestamp}]");
                    sb.AppendLine();
                    sb.AppendLine("```json");
                    sb.AppendLine(Scrub(entry.ToolArgs, redactSecrets));
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
                    sb.AppendLine(Truncate(content, 2000));
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }
            else if (entry.Role == MessageRole.System)
            {
                sb.AppendLine($"### ⚙️ System [{timestamp}]");
                sb.AppendLine();
                sb.AppendLine($"_{content}_");
                sb.AppendLine();
            }
            else if (entry.Role == MessageRole.Notification)
            {
                sb.AppendLine($"> **ℹ️ Notification [{timestamp}]:** {content}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string Scrub(string value, bool redactSecrets)
        => redactSecrets ? TranscriptSecretRedactor.Redact(value) ?? value : value;

    private static string Truncate(string value, int maxLength)
        => value.Length > maxLength ? value[..maxLength] + "\n... (truncated)" : value;
}

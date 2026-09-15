using BotNexus.Gateway.Api.Controllers;

namespace BotNexus.Gateway.Api.Export;

/// <summary>Caller-selected content and privacy controls for a transcript export.</summary>
public sealed record ExportContentOptions(
    bool IncludeTools = true,
    bool IncludeThinking = false,
    bool IncludeSystemMessages = false,
    bool RedactSecrets = true)
{
    /// <summary>Safe portal defaults.</summary>
    public static ExportContentOptions Default { get; } = new();
}

/// <summary>Applies export content controls before either renderer sees the document.</summary>
public static class ExportContentFilter
{
    /// <summary>Returns a document containing only the content selected by <paramref name="options"/>.</summary>
    public static ExportDocument Apply(ExportDocument document, ExportContentOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        var entries = document.Entries
            .Where(entry => options.IncludeTools || !string.Equals(entry.Role, "tool", StringComparison.Ordinal))
            .Where(entry => options.IncludeSystemMessages || !string.Equals(entry.Role, "system", StringComparison.Ordinal))
            .Select(entry => Copy(entry, options.IncludeThinking ? entry.ThinkingContent : null))
            .ToList();
        var sessionIds = entries.Select(entry => entry.SessionId).ToHashSet(StringComparer.Ordinal);
        var sessions = document.Sessions
            .Where(session => sessionIds.Contains(session.SessionId))
            .Select(session => session with
            {
                MessageCount = entries.Count(entry => entry.Kind == "message" && entry.SessionId == session.SessionId)
            })
            .ToList();

        return document with
        {
            Instructions = options.IncludeSystemMessages ? document.Instructions : null,
            Entries = entries,
            Sessions = sessions,
            MessageCount = entries.Count(entry => entry.Kind == "message"),
            ToolCallCount = entries.Count(entry => entry.Kind == "message" && !string.IsNullOrEmpty(entry.ToolName) && !string.IsNullOrEmpty(entry.ToolArgs))
        };
    }

    private static ConversationHistoryEntry Copy(ConversationHistoryEntry entry, string? thinkingContent) => new()
    {
        Kind = entry.Kind,
        EntryId = entry.EntryId,
        SessionId = entry.SessionId,
        AgentId = entry.AgentId,
        Timestamp = entry.Timestamp,
        Role = entry.Role,
        Content = entry.Content,
        ToolName = entry.ToolName,
        ToolCallId = entry.ToolCallId,
        ToolArgs = entry.ToolArgs,
        ToolIsError = entry.ToolIsError,
        Reason = entry.Reason,
        IsFolded = entry.IsFolded,
        ThinkingContent = thinkingContent,
        SenderId = entry.SenderId,
        MessageKind = entry.MessageKind
    };
}

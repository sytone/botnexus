using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Tools;

/// <summary>Tool for saving long-term memory and daily notes.</summary>
public sealed class MemorySaveTool : ToolBase
{
    private readonly IMemoryStore _memoryStore;
    private readonly string _agentName;

    public MemorySaveTool(IMemoryStore memoryStore, string agentName = "default", ILogger? logger = null)
        : base(logger)
    {
        _memoryStore = memoryStore;
        _agentName = agentName;
    }

    /// <inheritdoc/>
    public override ToolDefinition Definition => new(
        "memory_save",
        "Save information to long-term memory or today's daily notes",
        new Dictionary<string, ToolParameterSchema>
        {
            ["content"] = new("string", "Memory content to save", Required: true),
            ["target"] = new("string", "Target file: 'memory' or 'daily' (default: daily)", Required: false,
                EnumValues: ["memory", "daily"])
        });

    /// <inheritdoc/>
    protected override async Task<string> ExecuteCoreAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var content = GetRequiredString(arguments, "content");
        var target = GetOptionalString(arguments, "target", "daily").Trim().ToLowerInvariant();

        return target switch
        {
            "memory" => await SaveToLongTermMemoryAsync(content, cancellationToken).ConfigureAwait(false),
            "daily" => await SaveToDailyMemoryAsync(content, cancellationToken).ConfigureAwait(false),
            _ => throw new ToolArgumentException("Invalid 'target'. Use 'memory' or 'daily'.")
        };
    }

    private async Task<string> SaveToLongTermMemoryAsync(string content, CancellationToken cancellationToken)
    {
        const string sectionHeading = "## Notes";
        var existing = await _memoryStore.ReadAsync(_agentName, "MEMORY", cancellationToken).ConfigureAwait(false);
        var entry = $"- {content.Trim()}{Environment.NewLine}";

        if (string.IsNullOrWhiteSpace(existing))
        {
            var initial = $"{sectionHeading}{Environment.NewLine}{Environment.NewLine}{entry}";
            await _memoryStore.WriteAsync(_agentName, "MEMORY", initial, cancellationToken).ConfigureAwait(false);
            return "Saved to MEMORY.md under '## Notes'.";
        }

        var updated = AppendUnderSection(existing, sectionHeading, entry);
        await _memoryStore.WriteAsync(_agentName, "MEMORY", updated, cancellationToken).ConfigureAwait(false);
        return "Saved to MEMORY.md under '## Notes'.";
    }

    private async Task<string> SaveToDailyMemoryAsync(string content, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var key = $"daily/{now:yyyy-MM-dd}";
        var entry = $"[{now:HH:mm}] {content.Trim()}{Environment.NewLine}";

        await _memoryStore.AppendAsync(_agentName, key, entry, cancellationToken).ConfigureAwait(false);
        return $"Saved to memory/daily/{now:yyyy-MM-dd}.md.";
    }

    private static string AppendUnderSection(string markdown, string heading, string entry)
    {
        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.Contains(heading, StringComparison.Ordinal))
        {
            var withSpacing = normalized.TrimEnd('\n');
            return $"{withSpacing}\n\n{heading}\n\n{entry}".Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        }

        var lines = normalized.Split('\n').ToList();
        var sectionIndex = lines.FindIndex(line => string.Equals(line.Trim(), heading, StringComparison.Ordinal));
        if (sectionIndex < 0)
            return $"{normalized.TrimEnd('\n')}\n\n{heading}\n\n{entry}".Replace("\n", Environment.NewLine, StringComparison.Ordinal);

        var insertIndex = lines.Count;
        for (var i = sectionIndex + 1; i < lines.Count; i++)
        {
            if (lines[i].StartsWith("## ", StringComparison.Ordinal))
            {
                insertIndex = i;
                break;
            }
        }

        var entryLines = entry.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        if (insertIndex > 0 && !string.IsNullOrWhiteSpace(lines[insertIndex - 1]))
            lines.Insert(insertIndex++, string.Empty);

        foreach (var line in entryLines)
            lines.Insert(insertIndex++, line);

        return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
    }
}

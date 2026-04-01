using System.Globalization;
using System.Text;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Tools;

/// <summary>Tool for searching long-term memory and daily notes.</summary>
public sealed class MemorySearchTool : ToolBase
{
    private readonly IMemoryStore _memoryStore;
    private readonly string _agentName;

    public MemorySearchTool(IMemoryStore memoryStore, string agentName = "default", ILogger? logger = null)
        : base(logger)
    {
        _memoryStore = memoryStore;
        _agentName = agentName;
    }

    /// <inheritdoc/>
    public override ToolDefinition Definition => new(
        "memory_search",
        "Search across long-term memory and daily notes for relevant information",
        new Dictionary<string, ToolParameterSchema>
        {
            ["query"] = new("string", "Search query string", Required: true),
            ["max_results"] = new("integer", "Maximum number of results to return (default: 10)", Required: false)
        });

    /// <inheritdoc/>
    protected override async Task<string> ExecuteCoreAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var query = GetRequiredString(arguments, "query");
        var maxResults = Math.Max(1, GetOptionalInt(arguments, "max_results", 10));
        var keys = await _memoryStore.ListKeysAsync(_agentName, cancellationToken).ConfigureAwait(false);

        var orderedKeys = keys
            .Where(IsSearchableKey)
            .OrderByDescending(GetRecencyRank)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (orderedKeys.Count == 0)
            return "No memory files found. Expected MEMORY.md and/or memory/daily/YYYY-MM-DD.md files.";

        var results = new List<SearchResult>();
        foreach (var key in orderedKeys)
        {
            if (results.Count >= maxResults)
                break;

            var content = await _memoryStore.ReadAsync(_agentName, key, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            var lines = SplitLines(content);
            for (var i = 0; i < lines.Length && results.Count < maxResults; i++)
            {
                if (lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var start = Math.Max(0, i - 2);
                var end = Math.Min(lines.Length - 1, i + 2);
                var contextLines = Enumerable.Range(start, end - start + 1)
                    .Select(index => $"{index + 1,4}: {lines[index]}")
                    .ToArray();

                results.Add(new SearchResult(GetDisplayFileName(key), i + 1, contextLines));
            }
        }

        if (results.Count == 0)
            return $"No matches found for '{query}' in memory files.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} result(s) for '{query}':");
        sb.AppendLine();

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            sb.AppendLine($"[{i + 1}] {result.FileName} (match line {result.LineNumber})");
            foreach (var line in result.ContextLines)
                sb.AppendLine(line);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string[] SplitLines(string content)
        => content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static bool IsSearchableKey(string key)
        => key.Equals("MEMORY", StringComparison.OrdinalIgnoreCase) ||
           key.StartsWith("daily/", StringComparison.OrdinalIgnoreCase);

    private static long GetRecencyRank(string key)
    {
        if (key.Equals("MEMORY", StringComparison.OrdinalIgnoreCase))
            return long.MinValue;

        const string dailyPrefix = "daily/";
        if (key.StartsWith(dailyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var datePart = key[dailyPrefix.Length..];
            if (DateOnly.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return parsed.DayNumber;
        }

        return long.MinValue + 1;
    }

    private static string GetDisplayFileName(string key)
        => key.Equals("MEMORY", StringComparison.OrdinalIgnoreCase)
            ? "MEMORY.md"
            : $"memory/{key}.md";

    private sealed record SearchResult(string FileName, int LineNumber, IReadOnlyList<string> ContextLines);
}

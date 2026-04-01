using System.Globalization;
using System.Text;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Tools;

/// <summary>Tool for retrieving long-term memory and daily notes.</summary>
public sealed class MemoryGetTool : ToolBase
{
    private readonly IMemoryStore _memoryStore;
    private readonly string _agentName;

    public MemoryGetTool(IMemoryStore memoryStore, string agentName = "default", ILogger? logger = null)
        : base(logger)
    {
        _memoryStore = memoryStore;
        _agentName = agentName;
    }

    /// <inheritdoc/>
    public override ToolDefinition Definition => new(
        "memory_get",
        "Read long-term memory or a specific daily notes file",
        new Dictionary<string, ToolParameterSchema>
        {
            ["file"] = new("string", "Optional target: 'memory' or date like YYYY-MM-DD", Required: false),
            ["lines"] = new("string", "Optional line range like '10-20'", Required: false)
        });

    /// <inheritdoc/>
    protected override async Task<string> ExecuteCoreAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var fileArg = GetOptionalString(arguments, "file", "memory").Trim();
        var linesArg = GetOptionalString(arguments, "lines");

        var (key, displayName) = ResolveTarget(fileArg);
        var content = await _memoryStore.ReadAsync(_agentName, key, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(content))
            return $"No memory file found for '{displayName}'.";

        if (string.IsNullOrWhiteSpace(linesArg))
            return $"# {displayName}{Environment.NewLine}{Environment.NewLine}{content}".TrimEnd();

        var (start, end) = ParseLineRange(linesArg);
        var selected = SelectLineRange(content, start, end);

        if (selected.Length == 0)
            return $"Requested line range {start}-{end} is outside file bounds for '{displayName}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"# {displayName} (lines {start}-{end})");
        sb.AppendLine();
        foreach (var line in selected)
            sb.AppendLine(line);
        return sb.ToString().TrimEnd();
    }

    private static (string Key, string DisplayName) ResolveTarget(string fileArg)
    {
        if (string.IsNullOrWhiteSpace(fileArg) || fileArg.Equals("memory", StringComparison.OrdinalIgnoreCase))
            return ("MEMORY", "MEMORY.md");

        if (DateOnly.TryParseExact(fileArg, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return ($"daily/{fileArg}", $"memory/daily/{fileArg}.md");

        throw new ToolArgumentException("Invalid 'file'. Use 'memory' or a date like YYYY-MM-DD.");
    }

    private static (int Start, int End) ParseLineRange(string value)
    {
        var parts = value.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var start) ||
            !int.TryParse(parts[1], out var end) ||
            start < 1 || end < start)
        {
            throw new ToolArgumentException("Invalid 'lines' format. Use '<start>-<end>' (e.g., '10-20').");
        }

        return (start, end);
    }

    private static string[] SelectLineRange(string content, int start, int end)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (start > lines.Length)
            return [];

        var maxEnd = Math.Min(end, lines.Length);
        return Enumerable.Range(start, maxEnd - start + 1)
            .Select(lineNumber => $"{lineNumber,4}: {lines[lineNumber - 1]}")
            .ToArray();
    }
}

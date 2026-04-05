using System.Text.Json;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent.Utils;
using BotNexus.Providers.Core.Models;

namespace BotNexus.CodingAgent.Tools;

public sealed class ListDirectoryTool : IAgentTool
{
    private const int DefaultDepth = 2;
    private readonly string _workingDirectory;

    public ListDirectoryTool(string workingDirectory)
    {
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory))
            : Path.GetFullPath(workingDirectory);
    }

    public string Name => "list_directory";
    public string Label => "List Directory";

    public Tool Definition => new(
        Name,
        "List directory entries as a formatted tree with depth control.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "path": { "type": "string" },
                "depth": { "type": "integer" },
                "showHidden": { "type": "boolean" }
              },
              "required": ["path"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ReadRequiredString(arguments, "path");
        var prepared = new Dictionary<string, object?>(StringComparer.Ordinal) { ["path"] = path };
        if (arguments.TryGetValue("depth", out var depthObj) && depthObj is not null)
        {
            var depth = ReadInt(depthObj, "depth");
            if (depth < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(arguments), "depth must be >= 0.");
            }

            prepared["depth"] = depth;
        }

        if (arguments.TryGetValue("showHidden", out var showHiddenObj) && showHiddenObj is not null)
        {
            prepared["showHidden"] = ReadBool(showHiddenObj, "showHidden");
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(prepared);
    }

    public Task<AgentToolResult> ExecuteAsync(string toolCallId, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rawPath = arguments["path"]?.ToString() ?? throw new ArgumentException("Missing required argument: path.");
        var depth = arguments.TryGetValue("depth", out var depthObj) && depthObj is int parsedDepth ? parsedDepth : DefaultDepth;
        var showHidden = arguments.TryGetValue("showHidden", out var showHiddenObj) && showHiddenObj is bool parsedShowHidden && parsedShowHidden;

        var resolvedPath = PathUtils.ResolvePath(rawPath, _workingDirectory);
        if (!Directory.Exists(resolvedPath))
        {
            return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, $"Path '{rawPath}' does not exist or is not a directory.")]));
        }

        var rootDisplay = PathUtils.GetRelativePath(resolvedPath, _workingDirectory);
        if (string.IsNullOrWhiteSpace(rootDisplay) || rootDisplay == ".")
        {
            rootDisplay = ".";
        }

        var lines = new List<string> { $"{rootDisplay}{Path.DirectorySeparatorChar}" };
        AppendDirectoryTree(lines, resolvedPath, string.Empty, depth, showHidden, cancellationToken);
        if (lines.Count == 1)
        {
            return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, $"Directory '{rootDisplay}' is empty.")]));
        }

        return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, string.Join(Environment.NewLine, lines))]));
    }

    private void AppendDirectoryTree(ICollection<string> lines, string currentDirectory, string prefix, int depthRemaining, bool showHidden, CancellationToken cancellationToken)
    {
        if (depthRemaining <= 0)
        {
            return;
        }

        var entries = Directory.EnumerateFileSystemEntries(currentDirectory)
            .Where(entry => showHidden || !IsHiddenEntry(entry))
            .Where(entry => !PathUtils.IsGitIgnored(entry, _workingDirectory))
            .OrderBy(entry => Directory.Exists(entry) ? 0 : 1)
            .ThenBy(entry => Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = entries[index];
            var isDirectory = Directory.Exists(entry);
            var isLast = index == entries.Count - 1;
            var connector = isLast ? "└── " : "├── ";
            var name = Path.GetFileName(entry);

            if (isDirectory)
            {
                lines.Add($"{prefix}{connector}{name}{Path.DirectorySeparatorChar} [dir]");
                var nextPrefix = isLast ? $"{prefix}    " : $"{prefix}│   ";
                AppendDirectoryTree(lines, entry, nextPrefix, depthRemaining - 1, showHidden, cancellationToken);
                continue;
            }

            var size = 0L;
            try
            {
                size = new FileInfo(entry).Length;
            }
            catch
            {
            }

            lines.Add($"{prefix}{connector}{name} [file, {size} bytes]");
        }
    }

    private static bool IsHiddenEntry(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith(".", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return (File.GetAttributes(path) & FileAttributes.Hidden) == FileAttributes.Hidden;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadRequiredString(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            throw new ArgumentException($"Missing required argument: {key}.");
        }

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? throw new ArgumentException($"Argument '{key}' cannot be null."),
            JsonElement element => element.ToString(),
            _ => value.ToString() ?? throw new ArgumentException($"Argument '{key}' is invalid.")
        };
    }

    private static int ReadInt(object value, string key)
    {
        return value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var parsedInt) => parsedInt,
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var parsedText) => parsedText,
            string text when int.TryParse(text, out var parsedText) => parsedText,
            _ => throw new ArgumentException($"Argument '{key}' must be an integer.")
        };
    }

    private static bool ReadBool(object value, string key)
    {
        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } element when bool.TryParse(element.GetString(), out var parsedBool) => parsedBool,
            string text when bool.TryParse(text, out var parsedBool) => parsedBool,
            _ => throw new ArgumentException($"Argument '{key}' must be a boolean.")
        };
    }
}

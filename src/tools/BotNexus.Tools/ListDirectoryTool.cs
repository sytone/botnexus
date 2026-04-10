using System.Text.Json;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.Tools.Utils;
using BotNexus.Providers.Core.Models;
using System.IO.Abstractions;

namespace BotNexus.Tools;

public sealed class ListDirectoryTool : IAgentTool
{
    private const int MaxEntries = 500;
    private const int DefaultLimit = MaxEntries;
    private const int MaxOutputBytes = 50 * 1024;
    private readonly string _workingDirectory;
    private readonly IFileSystem _fileSystem;

    public ListDirectoryTool(string workingDirectory, IFileSystem? fileSystem = null)
    {
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory))
            : Path.GetFullPath(workingDirectory);
        _fileSystem = fileSystem ?? new FileSystem();
    }

    public string Name => "ls";
    public string Label => "List Directory";

    public Tool Definition => new(
        Name,
        "List directory entries up to 2 levels deep in a sorted listing.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "path": { "type": "string" },
                "limit": { "type": "integer" }
              }
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (arguments.TryGetValue("path", out var pathObj) && pathObj is not null)
        {
            prepared["path"] = ReadRequiredString(arguments, "path");
        }

        if (arguments.TryGetValue("limit", out var limitObj) && limitObj is not null)
        {
            var limit = ReadInt(limitObj, "limit");
            if (limit <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(arguments), "limit must be greater than 0.");
            }

            prepared["limit"] = limit;
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(prepared);
    }

    public Task<AgentToolResult> ExecuteAsync(string toolCallId, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rawPath = arguments.TryGetValue("path", out var pathObj) && pathObj is not null
            ? pathObj.ToString()!
            : ".";
        var requestedLimit = arguments.TryGetValue("limit", out var limitObj) && limitObj is int parsedLimit
            ? parsedLimit
            : DefaultLimit;
        var limit = Math.Min(requestedLimit, MaxEntries);

        var resolvedPath = PathUtils.ResolvePath(rawPath, _workingDirectory, _fileSystem);
        if (!_fileSystem.Directory.Exists(resolvedPath))
        {
            return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, $"Path '{rawPath}' does not exist or is not a directory.")]));
        }

        var entries = EnumerateEntries(resolvedPath, limit, _fileSystem, cancellationToken, out var entryLimitReached);

        if (entries.Count == 0)
        {
            return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "(empty directory)")]));
        }

        var outputLines = new List<string>();
        var outputBytes = 0;
        var byteLimitReached = false;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lineBytes = System.Text.Encoding.UTF8.GetByteCount(entry + Environment.NewLine);
            if (outputBytes + lineBytes > MaxOutputBytes)
            {
                byteLimitReached = true;
                break;
            }

            outputLines.Add(entry);
            outputBytes += lineBytes;
        }

        var output = string.Join(Environment.NewLine, outputLines);
        var notices = new List<string>();
        if (entryLimitReached)
        {
            notices.Add($"{limit} entries limit reached");
        }

        if (byteLimitReached)
        {
            notices.Add($"{MaxOutputBytes} byte limit reached");
        }

        if (notices.Count > 0)
        {
            output = $"{output}{Environment.NewLine}{Environment.NewLine}[{string.Join(". ", notices)}]";
        }

        return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, output)]));
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

    private static List<string> EnumerateEntries(string resolvedPath, int limit, IFileSystem fileSystem, CancellationToken cancellationToken, out bool entryLimitReached)
    {
        var entries = new List<string>(Math.Min(limit, MaxEntries));
        entryLimitReached = false;

        var topDirectories = new List<string>();
        var topFiles = new List<string>();

        foreach (var entryPath in fileSystem.Directory.EnumerateFileSystemEntries(resolvedPath, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryName = Path.GetFileName(entryPath);
            if (string.IsNullOrEmpty(entryName))
            {
                continue;
            }

            if (fileSystem.Directory.Exists(entryPath))
            {
                topDirectories.Add(entryName);
            }
            else
            {
                topFiles.Add(entryName);
            }
        }

        topDirectories.Sort(StringComparer.OrdinalIgnoreCase);
        topFiles.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in topDirectories)
        {
            if (!TryAdd(entries, $"{directory}/", limit, ref entryLimitReached))
            {
                return entries;
            }

            var childDirectories = new List<string>();
            var childFiles = new List<string>();
            var directoryPath = Path.Combine(resolvedPath, directory);

            foreach (var childPath in fileSystem.Directory.EnumerateFileSystemEntries(directoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var childName = Path.GetFileName(childPath);
                if (string.IsNullOrEmpty(childName))
                {
                    continue;
                }

                if (fileSystem.Directory.Exists(childPath))
                {
                    childDirectories.Add(childName);
                }
                else
                {
                    childFiles.Add(childName);
                }
            }

            childDirectories.Sort(StringComparer.OrdinalIgnoreCase);
            childFiles.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (var childDirectory in childDirectories)
            {
                if (!TryAdd(entries, $"{directory}/{childDirectory}/", limit, ref entryLimitReached))
                {
                    return entries;
                }

                var grandchildDirectoryPath = Path.Combine(directoryPath, childDirectory);
                var grandchildFiles = fileSystem.Directory.EnumerateFiles(grandchildDirectoryPath, "*", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var grandchildFile in grandchildFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryAdd(entries, $"{directory}/{childDirectory}/{grandchildFile}", limit, ref entryLimitReached))
                    {
                        return entries;
                    }
                }
            }

            foreach (var childFile in childFiles)
            {
                if (!TryAdd(entries, $"{directory}/{childFile}", limit, ref entryLimitReached))
                {
                    return entries;
                }
            }
        }

        foreach (var file in topFiles)
        {
            if (!TryAdd(entries, file, limit, ref entryLimitReached))
            {
                return entries;
            }
        }

        return entries;
    }

    private static bool TryAdd(List<string> entries, string value, int limit, ref bool limitReached)
    {
        if (entries.Count >= limit)
        {
            limitReached = true;
            return false;
        }

        entries.Add(value);
        return true;
    }

}

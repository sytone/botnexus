using System.Text;
using System.Text.Json;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.Tools.Utils;
using BotNexus.Providers.Core.Models;
using System.IO.Abstractions;

namespace BotNexus.Tools;

public sealed class ReadTool : IAgentTool
{
    private const int MaxOutputLines = 2000;
    private const int MaxOutputBytes = 50 * 1024;
    private readonly string _workingDirectory;
    private readonly IFileSystem _fileSystem;

    public ReadTool(string workingDirectory, IFileSystem? fileSystem = null)
    {
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory))
            : Path.GetFullPath(workingDirectory);
        _fileSystem = fileSystem ?? new FileSystem();
    }

    public string Name => "read";

    public string Label => "Read File";

    public Tool Definition => new(
        Name,
        "Read file content with optional offset/limit, or list directory entries.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "path": {
                  "type": "string",
                  "description": "File or directory path relative to working directory."
                },
                "offset": {
                  "type": "integer",
                  "description": "Line number to start reading from (1-indexed)."
                },
                "limit": {
                  "type": "integer",
                  "description": "Maximum number of lines to read."
                }
              },
              "required": ["path"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ConvertToString(arguments.TryGetValue("path", out var rawPath) ? rawPath : null, "path");
        var prepared = new Dictionary<string, object?>(StringComparer.Ordinal) { ["path"] = path };

        if (arguments.TryGetValue("offset", out var rawOffset) && rawOffset is not null)
        {
            var offset = ConvertToInt(rawOffset, "offset");
            if (offset < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(arguments), "offset must be >= 1.");
            }

            prepared["offset"] = offset;
        }

        if (arguments.TryGetValue("limit", out var rawLimit) && rawLimit is not null)
        {
            var limit = ConvertToInt(rawLimit, "limit");
            if (limit < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(arguments), "limit must be >= 1.");
            }

            prepared["limit"] = limit;
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(prepared);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var relativePath = arguments["path"]?.ToString()
                           ?? throw new ArgumentException("Missing required argument: path.");
        var resolvedPath = PathUtils.ResolvePath(relativePath, _workingDirectory, _fileSystem);

        if (_fileSystem.File.Exists(resolvedPath))
        {
            var bytes = _fileSystem.File.ReadAllBytes(resolvedPath);
            if (TryGetImageMimeType(resolvedPath, bytes, out var mimeType))
            {
                var imagePayload = EncodeImage(bytes, mimeType);
                var imageValue = $"data:{imagePayload.MimeType};base64,{imagePayload.Base64}";
                return new AgentToolResult(
                [
                    new AgentToolContent(AgentToolContentType.Text, $"Read image file [{imagePayload.MimeType}]"),
                    new AgentToolContent(AgentToolContentType.Image, imageValue)
                ]);
            }

            var textContent = Encoding.UTF8.GetString(bytes);
            var offset = arguments.TryGetValue("offset", out var offsetObj) && offsetObj is int parsedOffset ? parsedOffset : 1;
            var limit = arguments.TryGetValue("limit", out var limitObj) && limitObj is int parsedLimit ? parsedLimit : (int?)null;
            var content = ReadText(textContent, relativePath, offset, limit);
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, content)]);
        }

        if (_fileSystem.Directory.Exists(resolvedPath))
        {
            var listing = ListDirectory(resolvedPath, _fileSystem);
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, listing)]);
        }

        throw new FileNotFoundException($"Path '{relativePath}' does not exist.", resolvedPath);
    }

    private static string ReadText(string textContent, string path, int offset, int? limit)
    {
        var allLines = NormalizeLineEndings(textContent).Split('\n');
        var startLineIndex = Math.Max(0, offset - 1);
        if (startLineIndex >= allLines.Length)
        {
            throw new InvalidOperationException($"Offset {offset} is beyond end of file ({allLines.Length} lines total).");
        }

        var selectedLines = limit.HasValue
            ? allLines.Skip(startLineIndex).Take(limit.Value).ToList()
            : allLines.Skip(startLineIndex).ToList();

        if (selectedLines.Count > 0 && Encoding.UTF8.GetByteCount(selectedLines[0]) > MaxOutputBytes)
        {
            var firstLineSize = Encoding.UTF8.GetByteCount(selectedLines[0]);
            return $"[Line {offset} is {firstLineSize} bytes, exceeds {MaxOutputBytes} limit. Use bash to read a partial slice.]";
        }

        var output = new StringBuilder();
        var emittedBytes = 0;
        var emittedLines = 0;
        var totalLines = allLines.Length;
        var truncatedByLines = false;
        var truncatedByBytes = false;

        foreach (var line in selectedLines)
        {
            if (emittedLines >= MaxOutputLines)
            {
                truncatedByLines = true;
                break;
            }

            var text = line + Environment.NewLine;
            var lineBytes = Encoding.UTF8.GetByteCount(text);
            if (emittedBytes + lineBytes > MaxOutputBytes)
            {
                truncatedByBytes = true;
                break;
            }

            output.Append(text);
            emittedLines++;
            emittedBytes += lineBytes;
        }

        var outputText = output.ToString().TrimEnd();
        if (truncatedByLines || truncatedByBytes)
        {
            var endLine = offset + emittedLines - 1;
            var nextOffset = endLine + 1;
            if (truncatedByLines)
            {
                outputText += $"{Environment.NewLine}{Environment.NewLine}[Showing lines {offset}-{endLine} of {totalLines}. Use offset={nextOffset} to continue.]";
            }
            else
            {
                outputText += $"{Environment.NewLine}{Environment.NewLine}[Showing lines {offset}-{endLine} of {totalLines} ({MaxOutputBytes} byte limit). Use offset={nextOffset} to continue.]";
            }
        }
        else if (limit.HasValue && startLineIndex + selectedLines.Count < allLines.Length)
        {
            var nextOffset = startLineIndex + selectedLines.Count + 1;
            var remaining = allLines.Length - (startLineIndex + selectedLines.Count);
            outputText += $"{Environment.NewLine}{Environment.NewLine}[{remaining} more lines in file. Use offset={nextOffset} to continue.]";
        }

        return outputText;
    }

    private static (string Base64, string MimeType) EncodeImage(byte[] bytes, string mimeType)
    {
        return (Convert.ToBase64String(bytes), mimeType);
    }

    private static bool TryGetImageMimeType(string fullPath, byte[] bytes, out string mimeType)
    {
        mimeType = string.Empty;
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (extension == ".svg")
        {
            return false;
        }

        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            mimeType = "image/png";
            return true;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            mimeType = "image/jpeg";
            return true;
        }

        if (bytes.Length >= 6)
        {
            var header = Encoding.ASCII.GetString(bytes, 0, 6);
            if (header is "GIF87a" or "GIF89a")
            {
                mimeType = "image/gif";
                return true;
            }
        }

        if (bytes.Length >= 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            mimeType = "image/webp";
            return true;
        }

        return false;
    }

    private static string ListDirectory(string fullPath, IFileSystem fileSystem)
    {
        var root = Path.GetFullPath(fullPath);
        var entries = fileSystem.Directory
            .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Where(path => GetDepth(root, path) <= 2)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(root, path);
                return fileSystem.Directory.Exists(path) ? $"{relative}{Path.DirectorySeparatorChar}" : relative;
            })
            .ToList();

        return entries.Count == 0
            ? $"Directory '{root}' is empty (within depth 2)."
            : string.Join(Environment.NewLine, entries);
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static int GetDepth(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static string ConvertToString(object? value, string argumentName)
    {
        return value switch
        {
            null => throw new ArgumentException($"Argument '{argumentName}' cannot be null."),
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()
                ?? throw new ArgumentException($"Argument '{argumentName}' cannot be null."),
            JsonElement element => element.ToString(),
            _ => value.ToString() ?? throw new ArgumentException($"Argument '{argumentName}' is invalid.")
        };
    }

    private static int ConvertToInt(object value, string argumentName)
    {
        return value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var parsedInt) => parsedInt,
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var parsedText) => parsedText,
            string text when int.TryParse(text, out var parsedText) => parsedText,
            _ => throw new ArgumentException($"Argument '{argumentName}' must be an integer.")
        };
    }
}

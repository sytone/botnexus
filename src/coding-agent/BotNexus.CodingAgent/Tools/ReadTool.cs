using System.Text;
using System.Text.Json;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent.Utils;
using BotNexus.Providers.Core.Models;

namespace BotNexus.CodingAgent.Tools;

/// <summary>
/// Reads repository files and directories with deterministic formatting for model consumption.
/// </summary>
/// <remarks>
/// <para>
/// Contract: every resolved path must stay within the configured working directory. Traversal attempts are
/// rejected by <see cref="PathUtils.ResolvePath(string, string)"/> before any filesystem access is attempted.
/// </para>
/// <para>
/// For file reads, output uses line-numbered records (<c>N | content</c>) so follow-up edit operations can
/// reference stable coordinates. To protect token budget and latency, reads are capped at 2000 output lines.
/// </para>
/// </remarks>
public sealed class ReadTool : IAgentTool
{
    private const int MaxOutputLines = 2000;
    private const int MaxOutputBytes = 50_000;
    private readonly string _workingDirectory;

    /// <summary>
    /// Initializes the read tool.
    /// </summary>
    /// <param name="workingDirectory">Repository root used for path resolution and containment checks.</param>
    public ReadTool(string workingDirectory)
    {
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory))
            : Path.GetFullPath(workingDirectory);
    }

    /// <inheritdoc />
    public string Name => "read";

    /// <inheritdoc />
    public string Label => "Read File";

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Read file content with line numbers or list directory entries.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "path": {
                  "type": "string",
                  "description": "File or directory path relative to working directory."
                },
                "start_line": {
                  "type": "integer",
                  "description": "Optional 1-based start line for file reads."
                },
                "end_line": {
                  "type": "integer",
                  "description": "Optional 1-based inclusive end line for file reads."
                }
              },
              "required": ["path"]
            }
            """).RootElement.Clone());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!arguments.TryGetValue("path", out var rawPath))
        {
            throw new ArgumentException("Missing required argument: path.");
        }

        var path = ConvertToString(rawPath, "path");
        var prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["path"] = path
        };

        if (arguments.TryGetValue("start_line", out var rawStart) && rawStart is not null)
        {
            var startLine = ConvertToInt(rawStart, "start_line");
            if (startLine < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(arguments), "start_line must be >= 1.");
            }

            prepared["start_line"] = startLine;
        }

        if (arguments.TryGetValue("end_line", out var rawEnd) && rawEnd is not null)
        {
            var endLine = ConvertToInt(rawEnd, "end_line");
            if (endLine < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(arguments), "end_line must be >= 1.");
            }

            prepared["end_line"] = endLine;
        }

        if (prepared.TryGetValue("start_line", out var startObj)
            && prepared.TryGetValue("end_line", out var endObj)
            && startObj is int start
            && endObj is int end
            && end < start)
        {
            throw new ArgumentException("end_line must be greater than or equal to start_line.");
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(prepared);
    }

    /// <inheritdoc />
    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var relativePath = arguments["path"]?.ToString()
                           ?? throw new ArgumentException("Missing required argument: path.");
        var resolvedPath = PathUtils.ResolvePath(relativePath, _workingDirectory);

        if (File.Exists(resolvedPath))
        {
            if (TryGetImageMimeType(resolvedPath, out var mimeType))
            {
                var bytes = await File.ReadAllBytesAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
                var base64 = Convert.ToBase64String(bytes);
                var imageValue = $"data:{mimeType};base64,{base64}";
                var relativeResolvedPath = PathUtils.GetRelativePath(resolvedPath, _workingDirectory);
                var imageNote = $"Read image file '{relativeResolvedPath}' [{mimeType}] ({bytes.Length} bytes).";
                return new AgentToolResult(
                [
                    new AgentToolContent(AgentToolContentType.Text, imageNote),
                    new AgentToolContent(AgentToolContentType.Image, imageValue)
                ]);
            }

            var startLine = arguments.TryGetValue("start_line", out var startObj) && startObj is int start ? start : 1;
            var endLine = arguments.TryGetValue("end_line", out var endObj) && endObj is int end ? end : int.MaxValue;
            var content = await ReadFileAsync(resolvedPath, startLine, endLine, cancellationToken).ConfigureAwait(false);
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, content)]);
        }

        if (Directory.Exists(resolvedPath))
        {
            var listing = ListDirectory(resolvedPath);
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, listing)]);
        }

        throw new FileNotFoundException($"Path '{relativePath}' does not exist.", resolvedPath);
    }

    private static async Task<string> ReadFileAsync(
        string fullPath,
        int startLine,
        int endLine,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var absoluteLineNumber = 0;
        var emittedLines = 0;
        var emittedBytes = 0;
        var truncationMessage = string.Empty;
        var skippedBeforeStart = 0;

        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
            absoluteLineNumber++;

            if (absoluteLineNumber < startLine)
            {
                skippedBeforeStart++;
                continue;
            }

            if (absoluteLineNumber > endLine)
            {
                break;
            }

            if (emittedLines >= MaxOutputLines)
            {
                truncationMessage = $"[Output truncated at {MaxOutputLines} lines. Use start_line={absoluteLineNumber} to continue reading.]";
                break;
            }

            var formattedLine = $"{absoluteLineNumber} | {line}";
            var lineBytes = Encoding.UTF8.GetByteCount($"{formattedLine}{Environment.NewLine}");
            if (emittedBytes + lineBytes > MaxOutputBytes)
            {
                truncationMessage = $"[Output truncated at {MaxOutputBytes} bytes. Use start_line={absoluteLineNumber} to continue reading.]";
                break;
            }

            output.AppendLine(formattedLine);
            emittedLines++;
            emittedBytes += lineBytes;
        }

        if (output.Length == 0 && skippedBeforeStart == 0)
        {
            return $"File '{fullPath}' is empty.";
        }

        if (output.Length == 0)
        {
            return $"Requested range {startLine}-{endLine} returned no lines.";
        }

        if (!string.IsNullOrEmpty(truncationMessage))
        {
            output.AppendLine(truncationMessage);
        }

        return output.ToString().TrimEnd();
    }

    private static bool TryGetImageMimeType(string fullPath, out string mimeType)
    {
        mimeType = string.Empty;
        var extension = Path.GetExtension(fullPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        switch (extension.ToLowerInvariant())
        {
            case ".jpg":
            case ".jpeg":
                mimeType = "image/jpeg";
                return true;
            case ".png":
                mimeType = "image/png";
                return true;
            case ".gif":
                mimeType = "image/gif";
                return true;
            case ".webp":
                mimeType = "image/webp";
                return true;
            case ".svg":
                mimeType = "image/svg+xml";
                return true;
            default:
                return false;
        }
    }

    private static string ListDirectory(string fullPath)
    {
        var root = Path.GetFullPath(fullPath);
        var entries = Directory
            .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Where(path => GetDepth(root, path) <= 2)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(root, path);
                return Directory.Exists(path) ? $"{relative}{Path.DirectorySeparatorChar}" : relative;
            })
            .ToList();

        if (entries.Count == 0)
        {
            return $"Directory '{root}' is empty (within depth 2).";
        }

        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.AppendLine(entry);
        }

        return builder.ToString().TrimEnd();
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
            double d when Math.Abs(d % 1) < double.Epsilon => checked((int)d),
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var parsedInt) => parsedInt,
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var parsedText) => parsedText,
            string text when int.TryParse(text, out var parsedText) => parsedText,
            _ => throw new ArgumentException($"Argument '{argumentName}' must be an integer.")
        };
    }
}

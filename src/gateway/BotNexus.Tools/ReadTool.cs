using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Tools.Extensions;
using BotNexus.Tools.Utils;
using BotNexus.Agent.Providers.Core.Models;
using System.IO.Abstractions;

namespace BotNexus.Tools;

/// <summary>
/// Metadata describing the outcome of a <see cref="ReadTool"/> invocation. Exposed via
/// <see cref="AgentToolResult.Details"/> so a caller can capture an optimistic-concurrency token
/// (issue #2101) and pass it back to the <c>edit</c> tool as <c>expectedHash</c>; the edit then
/// detects that the file changed since it was read instead of blindly fuzzy-matching stale text.
/// </summary>
/// <param name="ConcurrencyToken">
/// A stable content token for the file that was read, or <c>null</c> for non-file reads
/// (directory listings and images).
/// </param>
public sealed record ReadResultDetails(string? ConcurrencyToken);

/// <summary>
/// A previously returned read of one file slice, retained for the lifetime of a single
/// <see cref="ReadTool"/> instance (issue #2689). The tool is constructed once per agent handle, so
/// the instance lifetime IS the session lifetime - there is deliberately no cross-session store.
/// </summary>
/// <param name="ContentToken">Content token of the WHOLE decoded file at the time of that read.</param>
/// <param name="RenderedLength">Character length of the slice that was returned to the model.</param>
internal sealed record PreviousRead(string ContentToken, int RenderedLength);


/// <summary>
/// Represents read tool.
/// </summary>
public sealed class ReadTool : IAgentTool
{
    private const int MaxOutputLines = 2000;
    private const int MaxOutputBytes = 50 * 1024;
    private readonly string _workingDirectory;
    private readonly IPathValidator? _validator;
    private readonly IFileSystem _fileSystem;
    private readonly ReadToolOptions _options;

    /// <summary>
    /// Per-session record of what each (path, offset, limit) slice last returned. Instance-scoped
    /// because the tool instance is created once per agent handle (#2689). Concurrent tool calls
    /// within one session are possible, so the map is concurrent.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PreviousRead> _previousReads =
        new(StringComparer.Ordinal);

    public ReadTool(string workingDirectory, IFileSystem? fileSystem = null)
        : this(workingDirectory, validator: null, fileSystem)
    {
    }

    public ReadTool(string workingDirectory, IPathValidator? validator, IFileSystem? fileSystem = null, ReadToolOptions? options = null)
    {
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory))
            : Path.GetFullPath(workingDirectory);
        _validator = validator;
        _fileSystem = fileSystem ?? new FileSystem();
        _options = options ?? new ReadToolOptions();
    }

    public string Name => "read";

    public string Label => "Read File";

    /// <summary>
    /// Executes new.
    /// </summary>
    /// <param name="Name">The name.</param>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The new result.</returns>
    public Tool Definition => new(
        Name,
        "Read file content with optional offset/limit, or list directory entries. Prefer offset/limit over whole-file reads on large files, and do not re-read a file you have already read in this session unless it changed.",
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

    /// <summary>
    /// Executes prepare arguments async.
    /// </summary>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The prepare arguments async result.</returns>
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

    /// <summary>
    /// Executes execute async.
    /// </summary>
    /// <param name="toolCallId">The tool call id.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The execute async result.</returns>
    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var relativePath = arguments["path"]?.ToString()
                           ?? throw new ArgumentException("Missing required argument: path.");
        var resolvedPath = _validator?.ValidateAndResolve(relativePath, FileAccessMode.Read);
        if (_validator is not null && resolvedPath is null)
        {
            return new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, $"Access denied: path '{relativePath}' is not permitted for read")]);
        }

        resolvedPath ??= PathUtils.ResolvePath(relativePath, _workingDirectory, _fileSystem);

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

            // UTF-8 first, then the host ANSI code page for legacy text files, so a windows-1252 /
            // shift_jis / gbk file is not returned as mojibake (the Windows code-page corruption class).
            var textContent = TextDecoder.DecodeBytes(bytes);
            var offset = arguments.TryGetValue("offset", out var offsetObj) && offsetObj is int parsedOffset ? parsedOffset : 1;
            var limit = arguments.TryGetValue("limit", out var limitObj) && limitObj is int parsedLimit ? parsedLimit : (int?)null;
            var content = ReadText(textContent, relativePath, offset, limit);
            // Surface an optimistic-concurrency token (issue #2101) so a later edit can detect the
            // file changed since this read. Computed over the whole decoded file, not the returned
            // slice, so the token is stable regardless of offset/limit paging.
            var token = ContentToken.Compute(textContent);

            // #2689 guardrail 2: an identical re-read of an unchanged slice returns a short marker.
            // The comparison is against the token of the content JUST READ FROM DISK, so a changed
            // file can never reach this branch - freshness is preserved by construction, not by a
            // staleness heuristic.
            var sliceKey = BuildSliceKey(resolvedPath, offset, limit);
            if (_options.ElideUnchangedRereads &&
                _previousReads.TryGetValue(sliceKey, out var previous) &&
                string.Equals(previous.ContentToken, token, StringComparison.Ordinal))
            {
                return new AgentToolResult(
                    [new AgentToolContent(AgentToolContentType.Text, BuildUnchangedNotice(relativePath, previous.RenderedLength, offset, limit))],
                    new ReadResultDetails(token));
            }

            _previousReads[sliceKey] = new PreviousRead(token, content.Length);

            // #2689 guardrail 1: name offset/limit on an oversized result so the NEXT call is cheap.
            content = AppendLargeReadNotice(content, relativePath, _options.LargeReadThresholdBytes);

            return new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, content)],
                new ReadResultDetails(token));
        }

        if (_fileSystem.Directory.Exists(resolvedPath))
        {
            var listing = ListDirectory(resolvedPath, _fileSystem);
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, listing)]);
        }

        throw new FileNotFoundException($"Path '{relativePath}' does not exist.", resolvedPath);
    }

    private static string BuildSliceKey(string resolvedPath, int offset, int? limit)
        => $"{resolvedPath.ToUpperInvariant()}|{offset}|{limit?.ToString() ?? "*"}";

    private static string BuildUnchangedNotice(string relativePath, int renderedLength, int offset, int? limit)
    {
        var slice = limit.HasValue
            ? $"lines {offset}-{offset + limit.Value - 1}"
            : offset > 1 ? $"from line {offset}" : "the whole file";
        return $"[Unchanged since your earlier read of '{relativePath}' ({slice}, {renderedLength} chars) in this session. "
             + "The file on disk is byte-for-byte identical to what you were already shown, so the content is not repeated. "
             + "Use the content you already have; pass a different offset/limit to see a different part of the file.]";
    }

    /// <summary>
    /// Appends an explicit size indicator when a returned read exceeds
    /// <paramref name="thresholdBytes"/>, naming <c>offset</c> and <c>limit</c> so the follow-up
    /// call can narrow instead of re-paying the whole file (#2689 AC1).
    /// </summary>
    private static string AppendLargeReadNotice(string content, string relativePath, int thresholdBytes)
    {
        if (thresholdBytes <= 0)
        {
            return content;
        }

        var sizeBytes = Encoding.UTF8.GetByteCount(content);
        if (sizeBytes <= thresholdBytes)
        {
            return content;
        }

        var lineCount = content.Length == 0 ? 0 : content.NormalizeLineEndings().Split('\n').Length;
        var notice = $"[Large read: '{relativePath}' returned {sizeBytes} bytes / {lineCount} lines, over the {thresholdBytes}-byte threshold. "
                   + "Narrow the next read with offset and limit (for example offset=1, limit=200) instead of re-reading the whole file, "
                   + "or use grep to locate the lines you need first.]";
        return content + Environment.NewLine + Environment.NewLine + notice;
    }

    private static string ReadText(string textContent, string path, int offset, int? limit)
    {
        var allLines = textContent.NormalizeLineEndings().Split('\n');
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
            double d => (int)d,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var parsedInt) => parsedInt,
            JsonElement { ValueKind: JsonValueKind.Number } element => (int)element.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var parsedText) => parsedText,
            JsonElement { ValueKind: JsonValueKind.String } element when double.TryParse(element.GetString(), out var parsedDouble) => (int)parsedDouble,
            string text when int.TryParse(text, out var parsedText) => parsedText,
            string text when double.TryParse(text, out var parsedDouble) => (int)parsedDouble,
            _ => throw new ArgumentException($"Argument '{argumentName}' must be an integer.")
        };
    }
}

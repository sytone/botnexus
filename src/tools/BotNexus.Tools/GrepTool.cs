using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Tools.Utils;
using BotNexus.Providers.Core.Models;
using Microsoft.Extensions.FileSystemGlobbing;
using System.IO.Abstractions;

namespace BotNexus.Tools;

/// <summary>
/// Searches file contents using regex pattern matching and returns matching lines.
/// </summary>
public sealed class GrepTool : IAgentTool
{
    private const int DefaultLimit = 100;
    private const int MaxOutputBytes = 50 * 1024;
    private const int MaxLineLength = 500;
    private const int BinaryProbeBytes = 4096;
    private readonly string _workingDirectory;
    private readonly IPathValidator? _validator;
    private readonly IFileSystem _fileSystem;

    public GrepTool(string workingDirectory, IFileSystem? fileSystem = null)
        : this(workingDirectory, validator: null, fileSystem)
    {
    }

    public GrepTool(string workingDirectory, IPathValidator? validator, IFileSystem? fileSystem = null)
    {
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory))
            : Path.GetFullPath(workingDirectory);
        _validator = validator;
        _fileSystem = fileSystem ?? new FileSystem();
    }

    public string Name => "grep";

    public string Label => "Grep Search";

    public Tool Definition => new(
        Name,
        "Search file contents using pattern matching. Returns matching lines with file paths and line numbers.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "pattern": { "type": "string", "description": "Search pattern (supports regex)" },
                "path": { "type": "string", "description": "Directory or file to search (default: working directory)" },
                "glob": { "type": "string", "description": "Glob pattern to include files (e.g., *.cs, *.ts)" },
                "ignore_case": { "type": "boolean", "description": "Perform case-insensitive matching (default: false)" },
                "ignoreCase": { "type": "boolean", "description": "Case-insensitive matching alias." },
                "literal": { "type": "boolean", "description": "Treat pattern as literal string (default: false)" },
                "context": { "type": "integer", "description": "Number of lines to show before and after each match (default: 0)" },
                "limit": { "type": "integer", "description": "Maximum results to return (default: 100)" }
              },
              "required": ["pattern"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pattern = ReadRequiredString(arguments, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ArgumentException("pattern cannot be empty.");
        }

        var prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["pattern"] = pattern
        };

        var literal = false;
        if (arguments.TryGetValue("literal", out var literalObj) && literalObj is not null)
        {
            literal = ReadBool(literalObj, "literal");
            prepared["literal"] = literal;
        }

        var effectivePattern = literal ? Regex.Escape(pattern) : pattern;
        try
        {
            _ = new Regex(effectivePattern, RegexOptions.Compiled);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Invalid regex pattern: {ex.Message}", nameof(arguments), ex);
        }

        if (arguments.TryGetValue("path", out var pathObj) && pathObj is not null)
        {
            prepared["path"] = ReadString(pathObj, "path");
        }

        if (arguments.TryGetValue("glob", out var globObj) && globObj is not null)
        {
            prepared["glob"] = ReadString(globObj, "glob");
        }
        else if (arguments.TryGetValue("include", out var includeObj) && includeObj is not null)
        {
            prepared["glob"] = ReadString(includeObj, "include");
        }

        if (arguments.TryGetValue("ignore_case", out var ignoreCaseObj) && ignoreCaseObj is not null)
        {
            prepared["ignore_case"] = ReadBool(ignoreCaseObj, "ignore_case");
        }
        else if (arguments.TryGetValue("ignoreCase", out var ignoreCaseAliasObj) && ignoreCaseAliasObj is not null)
        {
            prepared["ignore_case"] = ReadBool(ignoreCaseAliasObj, "ignoreCase");
        }

        if (arguments.TryGetValue("context", out var contextObj) && contextObj is not null)
        {
            var contextLines = ReadInt(contextObj, "context");
            if (contextLines < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(arguments), "context must be >= 0.");
            }

            prepared["context"] = contextLines;
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
        else if (arguments.TryGetValue("max_results", out var maxResultsObj) && maxResultsObj is not null)
        {
            var maxResults = ReadInt(maxResultsObj, "max_results");
            if (maxResults <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(arguments), "max_results must be greater than 0.");
            }

            prepared["limit"] = maxResults;
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

        var pattern = arguments["pattern"]?.ToString()
                      ?? throw new ArgumentException("Missing required argument: pattern.");
        var literal = arguments.TryGetValue("literal", out var literalObj) && literalObj is bool parsedLiteral && parsedLiteral;
        var effectivePattern = literal ? Regex.Escape(pattern) : pattern;
        var ignoreCase = arguments.TryGetValue("ignore_case", out var ignoreCaseObj) && ignoreCaseObj is bool parsedIgnoreCase && parsedIgnoreCase;
        var regex = new Regex(effectivePattern, ignoreCase ? RegexOptions.Compiled | RegexOptions.IgnoreCase : RegexOptions.Compiled);
        var contextLines = arguments.TryGetValue("context", out var contextObj) && contextObj is int parsedContext
            ? Math.Max(0, parsedContext)
            : 0;
        var maxResults = arguments.TryGetValue("limit", out var maxObj) && maxObj is int parsedMax
            ? parsedMax
            : DefaultLimit;
        var include = arguments.TryGetValue("glob", out var includeObj) ? includeObj?.ToString() : null;

        var rawPath = arguments.TryGetValue("path", out var pathObj) && pathObj is not null
            ? pathObj.ToString()!
            : ".";
        var targetPath = _validator?.ValidateAndResolve(rawPath, FileAccessMode.Read);
        if (_validator is not null && targetPath is null)
        {
            return new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, $"Access denied: path '{rawPath}' is not permitted for read")]);
        }

        targetPath ??= PathUtils.ResolvePath(rawPath, _workingDirectory, _fileSystem);

        if (!_fileSystem.Directory.Exists(targetPath) && !_fileSystem.File.Exists(targetPath))
        {
            return new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, $"Path '{targetPath}' does not exist.")]);
        }

        var matches = new List<string>(capacity: maxResults);
        var hadReadErrors = false;
        var matchCount = 0;

        var candidateFiles = EnumerateCandidateFiles(targetPath, include)
            .Where(file => !IsInsideGitDirectory(file, _workingDirectory))
            .Where(file => _validator?.CanRead(file) ?? true)
            .ToList();
        var ignoredFiles = PathUtils.GetGitIgnoredPaths(candidateFiles, _workingDirectory, _fileSystem);

        foreach (var file in candidateFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ignoredFiles.Contains(file) || IsBinaryFile(file))
            {
                continue;
            }

            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var allLines = new List<string>();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    allLines.Add(line);
                }

                for (var lineNumber = 1; lineNumber <= allLines.Count; lineNumber++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!regex.IsMatch(allLines[lineNumber - 1]))
                    {
                        continue;
                    }

                    var relativePath = PathUtils.GetRelativePath(file, _workingDirectory);
                    if (contextLines == 0)
                    {
                        matches.Add($"{relativePath}:{lineNumber}: {TruncateLine(allLines[lineNumber - 1])}");
                    }
                    else
                    {
                        var start = Math.Max(1, lineNumber - contextLines);
                        var end = Math.Min(allLines.Count, lineNumber + contextLines);
                        for (var contextLineNumber = start; contextLineNumber <= end; contextLineNumber++)
                        {
                            var separator = contextLineNumber == lineNumber ? ":" : "-";
                            matches.Add($"{relativePath}{separator}{contextLineNumber}{separator} {TruncateLine(allLines[contextLineNumber - 1])}");
                        }
                    }

                    matchCount++;
                    if (matchCount >= maxResults)
                    {
                        break;
                    }
                }
            }
            catch (IOException)
            {
                hadReadErrors = true;
            }
            catch (UnauthorizedAccessException)
            {
                hadReadErrors = true;
            }

            if (matchCount >= maxResults)
            {
                break;
            }
        }

        if (matches.Count == 0)
        {
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "No matches.")]);
        }

        var builder = new StringBuilder();
        var outputBytes = 0;
        var truncatedByBytes = false;
        foreach (var match in matches)
        {
            var line = $"{match}{Environment.NewLine}";
            var lineBytes = Encoding.UTF8.GetByteCount(line);
            if (outputBytes + lineBytes > MaxOutputBytes)
            {
                truncatedByBytes = true;
                break;
            }

            builder.Append(line);
            outputBytes += lineBytes;
        }

        if (matchCount >= maxResults)
        {
            builder.AppendLine($"[warning] Results truncated at {maxResults} matches.");
        }
        if (truncatedByBytes)
        {
            builder.AppendLine($"[warning] Results truncated at {MaxOutputBytes} bytes.");
        }

        if (hadReadErrors)
        {
            builder.AppendLine("[warning] Some files could not be read.");
        }

        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, builder.ToString().TrimEnd())]);
    }

    private IEnumerable<string> EnumerateCandidateFiles(string targetPath, string? include)
    {
        if (_fileSystem.File.Exists(targetPath))
        {
            if (MatchesIncludePattern(Path.GetFileName(targetPath), include))
            {
                yield return targetPath;
            }

            yield break;
        }

        foreach (var file in _fileSystem.Directory.EnumerateFiles(targetPath, "*", SearchOption.AllDirectories))
        {
            var relativeFromTarget = Path.GetRelativePath(targetPath, file);
            if (MatchesIncludePattern(relativeFromTarget, include))
            {
                yield return file;
            }
        }
    }

    private static bool MatchesIncludePattern(string relativePath, string? include)
    {
        if (string.IsNullOrWhiteSpace(include))
        {
            return true;
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(include);
        return matcher.Match(relativePath).HasMatches;
    }

    private static bool IsBinaryFile(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[Math.Min(BinaryProbeBytes, (int)Math.Min(stream.Length, BinaryProbeBytes))];
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            for (var index = 0; index < bytesRead; index++)
            {
                if (buffer[index] == 0)
                {
                    return true;
                }
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    private static bool IsInsideGitDirectory(string fullPath, string root)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return relative.StartsWith($".git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
               relative.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRequiredString(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            throw new ArgumentException($"Missing required argument: {key}.");
        }

        return ReadString(value, key);
    }

    private static string ReadString(object value, string key)
    {
        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()
                ?? throw new ArgumentException($"Argument '{key}' cannot be null."),
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
            double d => (int)d,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var parsedInt) => parsedInt,
            JsonElement { ValueKind: JsonValueKind.Number } element => (int)element.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var parsedText) => parsedText,
            JsonElement { ValueKind: JsonValueKind.String } element when double.TryParse(element.GetString(), out var parsedDouble) => (int)parsedDouble,
            string text when int.TryParse(text, out var parsedText) => parsedText,
            string text when double.TryParse(text, out var parsedDouble) => (int)parsedDouble,
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

    private static string TruncateLine(string line)
    {
        if (line.Length <= MaxLineLength)
        {
            return line;
        }

        return $"{line[..MaxLineLength]}... [truncated]";
    }
}

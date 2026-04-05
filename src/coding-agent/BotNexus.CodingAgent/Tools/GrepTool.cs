using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent.Utils;
using BotNexus.Providers.Core.Models;
using Microsoft.Extensions.FileSystemGlobbing;

namespace BotNexus.CodingAgent.Tools;

/// <summary>
/// Searches file contents using regex pattern matching and returns matching lines.
/// </summary>
public sealed class GrepTool : IAgentTool
{
    private const int DefaultMaxResults = 100;
    private const int MaxLineLength = 500;
    private const int BinaryProbeBytes = 4096;
    private readonly string _workingDirectory;

    public GrepTool(string workingDirectory)
    {
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory))
            : Path.GetFullPath(workingDirectory);
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
                "include": { "type": "string", "description": "Glob pattern to include files (e.g., *.cs, *.ts)" },
                "ignore_case": { "type": "boolean", "description": "Perform case-insensitive matching (default: false)" },
                "context": { "type": "integer", "description": "Number of lines to show before and after each match (default: 0)" },
                "max_results": { "type": "integer", "description": "Maximum results to return (default: 100)" }
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

        try
        {
            _ = new Regex(pattern, RegexOptions.Compiled);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Invalid regex pattern: {ex.Message}", nameof(arguments), ex);
        }

        var prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["pattern"] = pattern
        };

        if (arguments.TryGetValue("path", out var pathObj) && pathObj is not null)
        {
            prepared["path"] = ReadString(pathObj, "path");
        }

        if (arguments.TryGetValue("include", out var includeObj) && includeObj is not null)
        {
            prepared["include"] = ReadString(includeObj, "include");
        }

        if (arguments.TryGetValue("ignore_case", out var ignoreCaseObj) && ignoreCaseObj is not null)
        {
            prepared["ignore_case"] = ReadBool(ignoreCaseObj, "ignore_case");
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

        if (arguments.TryGetValue("max_results", out var maxResultsObj) && maxResultsObj is not null)
        {
            var maxResults = ReadInt(maxResultsObj, "max_results");
            if (maxResults <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(arguments), "max_results must be greater than 0.");
            }

            prepared["max_results"] = maxResults;
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
        var ignoreCase = arguments.TryGetValue("ignore_case", out var ignoreCaseObj) && ignoreCaseObj is bool parsedIgnoreCase && parsedIgnoreCase;
        var regex = new Regex(pattern, ignoreCase ? RegexOptions.Compiled | RegexOptions.IgnoreCase : RegexOptions.Compiled);
        var contextLines = arguments.TryGetValue("context", out var contextObj) && contextObj is int parsedContext
            ? Math.Max(0, parsedContext)
            : 0;
        var maxResults = arguments.TryGetValue("max_results", out var maxObj) && maxObj is int parsedMax
            ? parsedMax
            : DefaultMaxResults;
        var include = arguments.TryGetValue("include", out var includeObj) ? includeObj?.ToString() : null;

        var targetPath = arguments.TryGetValue("path", out var pathObj) && pathObj is not null
            ? PathUtils.ResolvePath(pathObj.ToString()!, _workingDirectory)
            : _workingDirectory;

        if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
        {
            return new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, $"Path '{targetPath}' does not exist.")]);
        }

        var matches = new List<string>(capacity: maxResults);
        var hadReadErrors = false;
        var matchCount = 0;

        foreach (var file in EnumerateCandidateFiles(targetPath, include))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (PathUtils.IsGitIgnored(file, _workingDirectory) || IsBinaryFile(file))
            {
                continue;
            }

            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var allLines = new List<string>();
                while (!reader.EndOfStream)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
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
        foreach (var match in matches)
        {
            builder.AppendLine(match);
        }

        if (matchCount >= maxResults)
        {
            builder.AppendLine($"[warning] Results truncated at {maxResults} matches.");
        }

        if (hadReadErrors)
        {
            builder.AppendLine("[warning] Some files could not be read.");
        }

        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, builder.ToString().TrimEnd())]);
    }

    private IEnumerable<string> EnumerateCandidateFiles(string targetPath, string? include)
    {
        if (File.Exists(targetPath))
        {
            if (MatchesIncludePattern(Path.GetFileName(targetPath), include))
            {
                yield return targetPath;
            }

            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(targetPath, "*", SearchOption.AllDirectories))
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

    private static string TruncateLine(string line)
    {
        if (line.Length <= MaxLineLength)
        {
            return line;
        }

        return $"{line[..MaxLineLength]}...";
    }
}

using BotNexus.Domain.Text;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Tools.Utils;
using BotNexus.Agent.Providers.Core.Models;
using Microsoft.Extensions.FileSystemGlobbing;
using System.IO.Abstractions;

namespace BotNexus.Tools;

/// <summary>
/// Searches file contents using regex pattern matching and returns matching lines.
/// </summary>
public sealed class GrepTool : IAgentTool
{
    private const int DefaultLimit = 100;

    /// <summary>
    /// Upper bound for the agent-supplied <c>limit</c>/<c>max_results</c> argument. The result list is
    /// pre-allocated with this value, so an unbounded <c>limit</c> would let a single call request a
    /// multi-billion-element list and throw <see cref="OutOfMemoryException"/> before any output-size
    /// protection runs. Clamping at read time keeps the allocation bounded. Matches GlobTool's fixed cap.
    /// </summary>
    private const int MaxLimit = 1000;
    private const int MaxOutputBytes = 50 * 1024;
    private const int MaxLineLength = 500;
    private const int BinaryProbeBytes = 4096;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);
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

    /// <summary>Content source classification for turn-taint accumulation (#2519). Matches from local files.</summary>
    public string ContentSource => ToolContentSource.Local;

    /// <summary>
    /// Executes new.
    /// </summary>
    /// <param name="Name">The name.</param>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The new result.</returns>
    /// <remarks>
    /// The schema is GENERATED from <see cref="GrepToolSchema"/> (#3320), not hand-written here. It is
    /// the same declaration that drives <see cref="PrepareArgumentsAsync"/>, so the two cannot disagree.
    /// </remarks>
    public Tool Definition => new(
        Name,
        "Search file contents using pattern matching. Returns matching lines with file paths and line numbers.",
        JsonDocument.Parse(GrepToolSchema.SchemaJson).RootElement.Clone());

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

        // The copy list is the GENERATED declaration order, not a hand-written sequence of
        // TryGetValue calls. Adding a parameter to GrepToolSchema makes it available here with no
        // second edit - the #2641 defect (declared in the schema, forgotten in the copy list) has no
        // representation. First writer wins, so a canonical key always beats its aliases.
        var prepared = new Dictionary<string, object?>(StringComparer.Ordinal);
        var sourceKeys = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var parameter in GrepToolSchema.Parameters)
        {
            if (!arguments.TryGetValue(parameter.Name, out var raw) || raw is null)
            {
                if (parameter.Required)
                {
                    throw new ArgumentException($"Missing required argument: {parameter.Name}.");
                }

                continue;
            }

            if (prepared.ContainsKey(parameter.TargetKey))
            {
                continue;
            }

            prepared[parameter.TargetKey] = parameter.JsonType switch
            {
                "boolean" => ReadBool(raw, parameter.Name),
                "integer" => ReadInt(raw, parameter.Name),
                _ => ReadString(raw, parameter.Name)
            };
            sourceKeys[parameter.TargetKey] = parameter.Name;
        }

        var pattern = (string)prepared["pattern"]!;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ArgumentException("pattern cannot be empty.");
        }

        var literal = prepared.TryGetValue("literal", out var literalObj) && literalObj is bool parsedLiteralArg && parsedLiteralArg;
        var effectivePattern = literal ? Regex.Escape(pattern) : pattern;
        try
        {
            _ = new Regex(effectivePattern, RegexOptions.Compiled, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Invalid regex pattern: {ex.Message}", nameof(arguments), ex);
        }

        // Range rules stay hand-written: they are per-parameter semantics, not schema shape, and the
        // spike deliberately does not try to generate them. The originating spelling is reported so
        // an alias caller still sees its own key name in the error.
        if (prepared.TryGetValue("context", out var contextObj) && contextObj is int contextLines && contextLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(arguments), "context must be >= 0.");
        }

        if (prepared.TryGetValue("limit", out var limitObj) && limitObj is int limit)
        {
            if (limit <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(arguments),
                    $"{sourceKeys["limit"]} must be greater than 0.");
            }

            prepared["limit"] = Math.Min(limit, MaxLimit);
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

        var pattern = arguments["pattern"]?.ToString()
                      ?? throw new ArgumentException("Missing required argument: pattern.");
        var literal = arguments.TryGetValue("literal", out var literalObj) && literalObj is bool parsedLiteral && parsedLiteral;
        var effectivePattern = literal ? Regex.Escape(pattern) : pattern;
        var ignoreCase = arguments.TryGetValue("ignore_case", out var ignoreCaseObj) && ignoreCaseObj is bool parsedIgnoreCase && parsedIgnoreCase;
        var regex = new Regex(effectivePattern, ignoreCase ? RegexOptions.Compiled | RegexOptions.IgnoreCase : RegexOptions.Compiled, RegexTimeout);
        var contextLines = arguments.TryGetValue("context", out var contextObj) && contextObj is int parsedContext
            ? Math.Max(0, parsedContext)
            : 0;
        var maxResults = arguments.TryGetValue("limit", out var maxObj) && maxObj is int parsedMax
            ? parsedMax
            : DefaultLimit;

        // Defence-in-depth: PrepareArgumentsAsync already clamps to [1, MaxLimit], but a direct caller
        // could pass an out-of-range value straight to ExecuteAsync. Clamp again before pre-allocating
        // the result list so capacity can never exceed MaxLimit (avoids OutOfMemoryException).
        maxResults = Math.Clamp(maxResults, 1, MaxLimit);
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

        // Resolution above may have swapped a symlinked prefix for the link's real target. That resolution
        // is the containment check and stays exactly as-is, but returning the resolved path would hand the
        // agent paths outside the tree it actually named. Capture the requested root so display paths can
        // be re-anchored onto it after enumeration. See issue #2384.
        var requestedRoot = ResolveRequestedRoot(rawPath);

        if (!_fileSystem.Directory.Exists(targetPath) && !_fileSystem.File.Exists(targetPath))
        {
            return new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, $"Path '{targetPath}' does not exist.")]);
        }

        var matches = new List<string>(capacity: maxResults);
        var hadReadErrors = false;
        var hadRegexTimeout = false;
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

                    var relativePath = PathUtils.GetRelativePath(
                        ReanchorToRequestedRoot(file, targetPath, requestedRoot),
                        _workingDirectory);
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
            catch (RegexMatchTimeoutException)
            {
                hadRegexTimeout = true;
                break;
            }

            if (matchCount >= maxResults)
            {
                break;
            }
        }

        if (matches.Count == 0)
        {
            if (hadRegexTimeout)
            {
                return new AgentToolResult(
                    [new AgentToolContent(AgentToolContentType.Text, "[warning] Pattern matching timed out -- the regex may have catastrophic backtracking. Simplify the pattern or use literal mode.")]);
            }

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

        if (hadRegexTimeout)
        {
            builder.AppendLine("[warning] Pattern matching timed out -- the regex may have catastrophic backtracking. Simplify the pattern or use literal mode.");
        }

        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, builder.ToString().TrimEnd())]);
    }

    /// <summary>
    /// Computes the absolute form of the caller's requested path <em>without</em> following symlinks, so
    /// result paths can be reported under the prefix the caller named. Returns <c>null</c> when the raw
    /// path uses a form this re-anchoring cannot faithfully mirror (home-relative <c>~</c> paths, or a
    /// path that does not exist as written), in which case the resolved path is reported unchanged.
    /// </summary>
    private string? ResolveRequestedRoot(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.TrimStart().StartsWith('~'))
        {
            return null;
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(
                Path.IsPathRooted(rawPath) ? rawPath : Path.Combine(_workingDirectory, rawPath));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }

        return _fileSystem.Directory.Exists(candidate) || _fileSystem.File.Exists(candidate)
            ? candidate
            : null;
    }

    /// <summary>
    /// Maps a file discovered under the symlink-resolved <paramref name="targetPath"/> back onto the prefix
    /// the caller asked about. Only the reported path changes - reading, access validation, and gitignore
    /// checks all continue to use the resolved path. The file is returned unchanged when resolution did not
    /// alter the prefix, when no requested root was captured, or when the file sits outside the resolved root.
    /// </summary>
    private static string ReanchorToRequestedRoot(string file, string targetPath, string? requestedRoot)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (requestedRoot is null || string.Equals(targetPath, requestedRoot, comparison))
        {
            return file;
        }

        var relative = Path.GetRelativePath(targetPath, file);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            return file;
        }

        // A single-file request resolves targetPath to the file itself; anchor onto the requested file path.
        return relative == "."
            ? requestedRoot
            : Path.Combine(requestedRoot, relative);
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

        return $"{TextTruncation.SafeTruncate(line, MaxLineLength)}... [truncated]";
    }
}

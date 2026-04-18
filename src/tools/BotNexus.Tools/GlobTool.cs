using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Tools.Utils;
using BotNexus.Agent.Providers.Core.Models;
using Microsoft.Extensions.FileSystemGlobbing;

namespace BotNexus.Tools;

/// <summary>
/// Expands glob patterns against repository files with .gitignore filtering.
/// </summary>
/// <remarks>
/// <para>
/// The matcher only returns filesystem paths under the configured working directory. Each candidate is
/// additionally checked via <see cref="PathUtils.GetGitIgnoredPaths(IEnumerable{string}, string, IFileSystem)"/> to keep ignored artifacts
/// (build output, local secrets, generated files) out of model-visible results.
/// </para>
/// <para>
/// Returned paths are normalized as working-directory-relative paths for stable downstream tool chaining.
/// </para>
/// </remarks>
public sealed class GlobTool : IAgentTool
{
    private const int MaxResults = 1000;
    private readonly string _workingDirectory;
    private readonly IPathValidator? _validator;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes the glob tool.
    /// </summary>
    /// <param name="workingDirectory">Repository root used as the default glob base.</param>
    /// <param name="fileSystem">File system abstraction for testability.</param>
    public GlobTool(string workingDirectory, IFileSystem? fileSystem = null)
        : this(workingDirectory, validator: null, fileSystem)
    {
    }

    /// <summary>
    /// Initializes the glob tool.
    /// </summary>
    /// <param name="workingDirectory">Repository root used as the default glob base.</param>
    /// <param name="validator">Path validator for access checks.</param>
    /// <param name="fileSystem">File system abstraction for testability.</param>
    public GlobTool(string workingDirectory, IPathValidator? validator, IFileSystem? fileSystem = null)
    {
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory))
            : Path.GetFullPath(workingDirectory);
        _validator = validator;
        _fileSystem = fileSystem ?? new FileSystem();
    }

    /// <inheritdoc />
    public string Name => "glob";

    /// <inheritdoc />
    public string Label => "Glob Files";

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Find files by glob pattern with optional base path.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "pattern": {
                  "type": "string",
                  "description": "Glob pattern, e.g. **/*.cs or src/**/*.md."
                },
                "path": {
                  "type": "string",
                  "description": "Optional base directory relative to working directory."
                }
              },
              "required": ["pattern"]
            }
            """).RootElement.Clone());

    /// <inheritdoc />
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

        if (arguments.TryGetValue("path", out var pathObj) && pathObj is not null)
        {
            prepared["path"] = ReadRequiredString(arguments, "path");
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(prepared);
    }

    /// <inheritdoc />
    public Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pattern = arguments["pattern"]?.ToString()
                      ?? throw new ArgumentException("Missing required argument: pattern.");
        var rawPath = arguments.TryGetValue("path", out var pathObj) && pathObj is not null
            ? pathObj.ToString()!
            : ".";
        var baseDirectory = _validator?.ValidateAndResolve(rawPath, FileAccessMode.Read);
        if (_validator is not null && baseDirectory is null)
        {
            return Task.FromResult(new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, $"Access denied: path '{rawPath}' is not permitted for read")]));
        }

        baseDirectory ??= PathUtils.ResolvePath(rawPath, _workingDirectory, _fileSystem);

        if (!_fileSystem.Directory.Exists(baseDirectory))
        {
            throw new DirectoryNotFoundException($"Base directory '{baseDirectory}' does not exist.");
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(pattern);

        // Matcher.GetResultsInFullPath requires real directory paths;
        // post-filter through IFileSystem for consistency.
        var allMatches = matcher.GetResultsInFullPath(baseDirectory)
            .Where(path => _fileSystem.File.Exists(path))
            .Select(path => Path.GetFullPath(path))
            .Where(path => _validator?.CanRead(path) ?? true)
            .ToList();
        var ignoredPaths = PathUtils.GetGitIgnoredPaths(allMatches, _workingDirectory);
        var matches = allMatches
            .Where(path => !ignoredPaths.Contains(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => PathUtils.GetRelativePath(path, _workingDirectory))
            .ToList();

        if (matches.Count == 0)
        {
            return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "No matches.")]));
        }

        var builder = new StringBuilder();
        var displayedMatches = matches.Take(MaxResults).ToList();
        foreach (var match in displayedMatches)
        {
            builder.AppendLine(match);
        }

        if (matches.Count > MaxResults)
        {
            builder.AppendLine($"[Showing first {MaxResults} of {matches.Count} matches]");
        }

        return Task.FromResult(new AgentToolResult(
            [new AgentToolContent(AgentToolContentType.Text, builder.ToString().TrimEnd())]));
    }

    private static string ReadRequiredString(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            throw new ArgumentException($"Missing required argument: {key}.");
        }

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()
                ?? throw new ArgumentException($"Argument '{key}' cannot be null."),
            JsonElement element => element.ToString(),
            _ => value.ToString() ?? throw new ArgumentException($"Argument '{key}' is invalid.")
        };
    }
}

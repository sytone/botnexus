using System.Text;
using System.Text.Json;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent.Utils;
using BotNexus.Providers.Core.Models;
using Microsoft.Extensions.FileSystemGlobbing;

namespace BotNexus.CodingAgent.Tools;

/// <summary>
/// Expands glob patterns against repository files with .gitignore filtering.
/// </summary>
/// <remarks>
/// <para>
/// The matcher only returns filesystem paths under the configured working directory. Each candidate is
/// additionally passed through <see cref="PathUtils.IsGitIgnored(string, string)"/> to keep ignored artifacts
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

    /// <summary>
    /// Initializes the glob tool.
    /// </summary>
    /// <param name="workingDirectory">Repository root used as the default glob base.</param>
    public GlobTool(string workingDirectory)
    {
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory))
            : Path.GetFullPath(workingDirectory);
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
        var baseDirectory = arguments.TryGetValue("path", out var pathObj) && pathObj is not null
            ? PathUtils.ResolvePath(pathObj.ToString()!, _workingDirectory)
            : _workingDirectory;

        if (!Directory.Exists(baseDirectory))
        {
            throw new DirectoryNotFoundException($"Base directory '{baseDirectory}' does not exist.");
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(pattern);

        var matches = matcher.GetResultsInFullPath(baseDirectory)
            .Where(path => File.Exists(path))
            .Where(path => !PathUtils.IsGitIgnored(path, _workingDirectory))
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

using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Tools.Utils;
using BotNexus.Agent.Providers.Core.Models;
using System.IO.Abstractions;

namespace BotNexus.Tools;

/// <summary>
/// Writes complete file content to disk inside the working directory boundary.
/// </summary>
/// <remarks>
/// <para>
/// This tool performs full-file replacement semantics: the provided content becomes the canonical
/// file body. Callers should use <c>read</c> first when they need merge-safe edits.
/// </para>
/// <para>
/// Parent directories are created automatically to support new-file authoring flows without requiring
/// separate directory management tool calls.
/// </para>
/// <para>
/// Setting <c>append</c> switches to append semantics (issue #3571). Append exists because the only
/// previous way to add a line to a log file was an anchored <c>edit</c>, and in an append-only log the
/// anchor text is boilerplate that recurs by design — so the anchor becomes ambiguous as the file grows
/// and the edit fails with <c>found N</c>. An append needs no anchor at all, so it cannot become
/// ambiguous, and it writes only the supplied bytes rather than re-emitting the whole file.
/// </para>
/// </remarks>
public sealed class WriteTool : IAgentTool
{
    private readonly string _workingDirectory;
    private readonly IPathValidator? _validator;
    private readonly FileMutationQueue _fileMutationQueue;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes the write tool.
    /// </summary>
    /// <param name="workingDirectory">Repository root used for secure path resolution.</param>
    public WriteTool(string workingDirectory, IFileSystem? fileSystem = null)
        : this(workingDirectory, validator: null, fileSystem)
    {
    }

    /// <summary>
    /// Initializes the write tool.
    /// </summary>
    /// <param name="workingDirectory">Repository root used for secure path resolution.</param>
    /// <param name="validator">Path validator for access checks.</param>
    public WriteTool(string workingDirectory, IPathValidator? validator, IFileSystem? fileSystem = null)
    {
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory))
            : Path.GetFullPath(workingDirectory);
        _validator = validator;
        _fileMutationQueue = FileMutationQueue.Shared;
        _fileSystem = fileSystem ?? new FileSystem();
    }

    /// <inheritdoc />
    public string Name => "write";

    /// <inheritdoc />
    public string Label => "Write File";

    /// <summary>Content source classification for turn-taint accumulation (#2519). Returns a locally generated write confirmation.</summary>
    public string ContentSource => ToolContentSource.Local;

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Write full file content, or append to the end of a file with append=true, creating parent directories as needed.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "path": {
                  "type": "string",
                  "description": "Target file path relative to working directory."
                },
                "content": {
                  "type": "string",
                  "description": "File content. Replaces the whole file, or is appended verbatim when append is true."
                },
                "append": {
                  "type": "boolean",
                  "description": "When true, append content to the end of the file instead of replacing it. Needs no anchor, never fails on ambiguity, and creates the file if absent. Use this for append-only logs and memory notes instead of an anchored edit."
                }
              },
              "required": ["path", "content"]
            }
            """).RootElement.Clone());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = ReadRequiredString(arguments, "path");
        var content = ReadRequiredString(arguments, "content");
        var append = ReadOptionalBool(arguments, "append");

        IReadOnlyDictionary<string, object?> prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["path"] = path,
            ["content"] = content,
            ["append"] = append
        };

        return Task.FromResult(prepared);
    }

    /// <inheritdoc />
    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var rawPath = arguments["path"]?.ToString()
                      ?? throw new ArgumentException("Missing required argument: path.");
        var content = arguments["content"]?.ToString()
                      ?? throw new ArgumentException("Missing required argument: content.");
        var append = ReadOptionalBool(arguments, "append");

        var fullPath = _validator?.ValidateAndResolve(rawPath, FileAccessMode.Write);
        if (_validator is not null && fullPath is null)
        {
            return new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, $"Access denied: path '{rawPath}' is not permitted for write")]);
        }

        fullPath ??= PathUtils.ResolvePath(rawPath, _workingDirectory, _fileSystem);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            _fileSystem.Directory.CreateDirectory(parent);
        }

        return await _fileMutationQueue.WithFileLockAsync(fullPath, async () =>
        {
            if (append)
            {
                // AppendAllTextAsync creates the file when absent and writes only the supplied bytes,
                // so the caller never has to read and re-emit existing content (issue #3571 clause 4).
                await _fileSystem.File.AppendAllTextAsync(fullPath, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _fileSystem.File.WriteAllTextAsync(fullPath, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }

            var byteCount = Encoding.UTF8.GetByteCount(content);
            var relativePath = PathUtils.GetRelativePath(fullPath, _workingDirectory);
            var message = append
                ? $"Appended {byteCount} bytes to '{relativePath}'."
                : $"Wrote '{relativePath}' ({byteCount} bytes).";

            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, message)]);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads an optional boolean flag that may arrive as a JSON bool, a JSON string ("true"), or an
    /// already-materialized CLR value, defaulting to <c>false</c> when absent. Providers vary in how
    /// they serialize booleans, so a strict cast would reject valid append calls.
    /// </summary>
    private static bool ReadOptionalBool(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            return false;
        }

        return value switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } element =>
                bool.TryParse(element.GetString(), out var parsed) && parsed,
            _ => bool.TryParse(value.ToString(), out var parsedValue) && parsedValue
        };
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

using System.Text;
using System.Text.Json;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent.Utils;
using BotNexus.Providers.Core.Models;

namespace BotNexus.CodingAgent.Tools;

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
/// </remarks>
public sealed class WriteTool : IAgentTool
{
    private readonly string _workingDirectory;
    private readonly FileMutationQueue _fileMutationQueue;

    /// <summary>
    /// Initializes the write tool.
    /// </summary>
    /// <param name="workingDirectory">Repository root used for secure path resolution.</param>
    public WriteTool(string workingDirectory)
    {
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory))
            : Path.GetFullPath(workingDirectory);
        _fileMutationQueue = FileMutationQueue.Shared;
    }

    /// <inheritdoc />
    public string Name => "write";

    /// <inheritdoc />
    public string Label => "Write File";

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Write full file content, creating parent directories as needed.",
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
                  "description": "Complete file content to write."
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

        IReadOnlyDictionary<string, object?> prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["path"] = path,
            ["content"] = content
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

        var fullPath = PathUtils.ResolvePath(rawPath, _workingDirectory);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        return await _fileMutationQueue.WithFileLockAsync(fullPath, async () =>
        {
            await File.WriteAllTextAsync(fullPath, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            var byteCount = Encoding.UTF8.GetByteCount(content);
            var relativePath = PathUtils.GetRelativePath(fullPath, _workingDirectory);
            var message = $"Wrote '{relativePath}' ({byteCount} bytes).";

            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, message)]);
        }).ConfigureAwait(false);
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

using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Abstractions.Agents;
using System.IO.Abstractions;

namespace BotNexus.Memory.Tools;

/// <summary>
/// Appends markdown notes to an agent's memory workspace files.
/// </summary>
public sealed class MemorySaveTool : IAgentTool
{
    private const string DefaultMemoryDirectory = "memory";
    private readonly IAgentWorkspaceManager _workspaceManager;
    private readonly IFileSystem _fileSystem;
    private readonly string _agentId;
    private readonly string? _memoryPathOverride;

    public MemorySaveTool(
        IAgentWorkspaceManager workspaceManager,
        string agentId,
        string? memoryPathOverride = null,
        IFileSystem? fileSystem = null)
    {
        _workspaceManager = workspaceManager;
        _agentId = string.IsNullOrWhiteSpace(agentId)
            ? throw new ArgumentException("Agent ID is required.", nameof(agentId))
            : agentId;
        _memoryPathOverride = memoryPathOverride;
        _fileSystem = fileSystem ?? new FileSystem();
    }

    public string Name => "memory_save";

    public string Label => "Memory Save";

    public Tool Definition => new(
        Name,
        "Append markdown memory notes. Use content only for today's daily note, or provide file_path for a specific note under memory root.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "content": {
                  "type": "string",
                  "description": "Content to append to the memory note"
                },
                "file_path": {
                  "type": "string",
                  "description": "Optional relative note path under memory root"
                }
              },
              "required": ["content"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!arguments.TryGetValue("content", out var contentValue) || string.IsNullOrWhiteSpace(ToStringValue(contentValue)))
            throw new ArgumentException("Missing required argument: content.");

        var prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["content"] = ToStringValue(contentValue)!
        };

        if (arguments.TryGetValue("file_path", out var filePathValue) && !string.IsNullOrWhiteSpace(ToStringValue(filePathValue)))
            prepared["file_path"] = ToStringValue(filePathValue);

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(prepared);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var content = ToStringValue(arguments["content"])!;
        var filePath = arguments.TryGetValue("file_path", out var filePathValue)
            ? ToStringValue(filePathValue)
            : null;

        var workspacePath = _workspaceManager.GetWorkspacePath(_agentId);
        var resolvedPath = ResolveTargetPath(workspacePath, filePath);
        var directory = _fileSystem.Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(directory))
            _fileSystem.Directory.CreateDirectory(directory);

        var normalizedContent = content.EndsWith(Environment.NewLine, StringComparison.Ordinal)
            ? content
            : $"{content}{Environment.NewLine}";
        await _fileSystem.File.AppendAllTextAsync(resolvedPath, normalizedContent, cancellationToken);

        var relativePath = _fileSystem.Path.GetRelativePath(workspacePath, resolvedPath).Replace('\\', '/');
        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, $"Appended memory note to {relativePath}.")]);
    }

    private string ResolveTargetPath(string workspacePath, string? filePath)
    {
        var workspaceFullPath = _fileSystem.Path.GetFullPath(workspacePath);
        var memoryRoot = ResolveMemoryRoot(workspaceFullPath);

        if (string.IsNullOrWhiteSpace(filePath))
            return ResolveDefaultTarget(memoryRoot);

        if (_fileSystem.Path.IsPathRooted(filePath))
            throw new ArgumentException("file_path must be relative to the memory root.", nameof(filePath));

        var normalized = NormalizeRelativePath(filePath);
        var resolved = _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(memoryRoot, normalized));
        EnsureWithinRoot(memoryRoot, resolved);
        return resolved;
    }

    private string ResolveDefaultTarget(string memoryRoot)
    {
        if (!string.IsNullOrWhiteSpace(_memoryPathOverride) &&
            _memoryPathOverride.Trim().Replace('\\', '/').EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var fixedFilePath = _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(
                _workspaceManager.GetWorkspacePath(_agentId),
                NormalizeRelativePath(_memoryPathOverride)));
            var root = _fileSystem.Path.GetDirectoryName(fixedFilePath) ?? memoryRoot;
            EnsureWithinRoot(root, fixedFilePath);
            return fixedFilePath;
        }

        return _fileSystem.Path.Combine(memoryRoot, $"{DateTime.Now:yyyy-MM-dd}.md");
    }

    private string ResolveMemoryRoot(string workspaceFullPath)
    {
        var overridePath = _memoryPathOverride?.Trim();
        var relativePath = string.IsNullOrWhiteSpace(overridePath) ? DefaultMemoryDirectory : NormalizeRelativePath(overridePath);
        if (relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            relativePath = _fileSystem.Path.GetDirectoryName(relativePath) ?? DefaultMemoryDirectory;

        var root = _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(workspaceFullPath, relativePath));
        EnsureWithinRoot(workspaceFullPath, root);
        return root;
    }

    private void EnsureWithinRoot(string root, string candidate)
    {
        var normalizedRoot = root.TrimEnd(_fileSystem.Path.DirectorySeparatorChar, _fileSystem.Path.AltDirectorySeparatorChar)
            + _fileSystem.Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !candidate.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Requested path escapes the allowed memory root.");
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Trim();
        if (normalized.StartsWith("memory/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("memory\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring("memory".Length + 1);
        }

        return normalized;
    }

    private static string? ToStringValue(object? value)
        => value switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
}

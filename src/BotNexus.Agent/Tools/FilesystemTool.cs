using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Tools;

/// <summary>Tool for reading and writing files in the agent workspace.</summary>
public sealed class FilesystemTool : ToolBase
{
    private readonly string _workspacePath;
    private readonly bool _restrictToWorkspace;

    public FilesystemTool(string workspacePath, bool restrictToWorkspace = true, ILogger? logger = null)
        : base(logger)
    {
        _workspacePath = workspacePath;
        _restrictToWorkspace = restrictToWorkspace;
    }

    /// <inheritdoc/>
    public override ToolDefinition Definition => new(
        "filesystem",
        "Read, write, list, or delete files. Use action='read', 'write', 'list', or 'delete'.",
        new Dictionary<string, ToolParameterSchema>
        {
            ["action"] = new("string", "Action: read, write, list, or delete", Required: true,
                EnumValues: ["read", "write", "list", "delete"]),
            ["path"] = new("string", "File or directory path", Required: true),
            ["content"] = new("string", "Content to write (for write action)", Required: false)
        });

    /// <inheritdoc/>
    protected override async Task<string> ExecuteCoreAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var action = GetOptionalString(arguments, "action", "read");
        var path = GetRequiredString(arguments, "path");
        var resolvedPath = ResolvePath(path);

        if (_restrictToWorkspace && !resolvedPath.StartsWith(_workspacePath, StringComparison.OrdinalIgnoreCase))
            throw new ToolArgumentException($"Access denied. Path must be within workspace: {_workspacePath}");

        return action.ToLowerInvariant() switch
        {
            "read" => await ReadFileAsync(resolvedPath, cancellationToken),
            "write" => await WriteFileAsync(resolvedPath, GetOptionalString(arguments, "content"), cancellationToken),
            "list" => ListDirectory(resolvedPath),
            "delete" => DeleteFile(resolvedPath),
            _ => throw new ToolArgumentException($"Unknown action '{action}'")
        };
    }

    private string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        return Path.GetFullPath(Path.Combine(_workspacePath, path));
    }

    private static async Task<string> ReadFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return $"Error: File not found: {path}";
        return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
    }

    private static async Task<string> WriteFileAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
        return $"Written {content.Length} bytes to {path}";
    }

    private static string ListDirectory(string path)
    {
        if (!Directory.Exists(path)) return $"Error: Directory not found: {path}";
        var entries = Directory.GetFileSystemEntries(path);
        return string.Join("\n", entries);
    }

    private static string DeleteFile(string path)
    {
        if (File.Exists(path)) { File.Delete(path); return $"Deleted: {path}"; }
        if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); return $"Deleted directory: {path}"; }
        return $"Error: Path not found: {path}";
    }
}

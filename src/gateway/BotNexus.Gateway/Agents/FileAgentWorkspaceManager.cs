using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Configuration;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Agents;

public sealed class FileAgentWorkspaceManager : IAgentWorkspaceManager
{
    private const string MemoryDirectoryName = "memory";
    private readonly BotNexusHome _botNexusHome;
    private readonly IFileSystem _fileSystem;

    public FileAgentWorkspaceManager(BotNexusHome botNexusHome, IFileSystem fileSystem)
    {
        _botNexusHome = botNexusHome;
        _fileSystem = fileSystem;
    }

    public async Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        var workspacePath = GetWorkspacePath(agentName);

        var soul = await ReadFileOrEmptyAsync(Path.Combine(workspacePath, "SOUL.md"), cancellationToken);
        var identity = await ReadFileOrEmptyAsync(Path.Combine(workspacePath, "IDENTITY.md"), cancellationToken);
        var user = await ReadFileOrEmptyAsync(Path.Combine(workspacePath, "USER.md"), cancellationToken);
        var memory = await ReadFileOrEmptyAsync(Path.Combine(workspacePath, "MEMORY.md"), cancellationToken);
        return new AgentWorkspace(agentName.Trim(), soul, identity, user, memory);
    }

    public async Task SaveMemoryAsync(string agentName, string content, CancellationToken cancellationToken = default)
        => await SaveMemoryAsync(agentName, filePath: null, content, memoryPathOverride: null, cancellationToken);

    public async Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken cancellationToken = default)
        => await SaveMemoryAsync(agentName, filePath, content, memoryPathOverride: null, cancellationToken);

    public async Task SaveMemoryAsync(
        string agentName,
        string? filePath,
        string content,
        string? memoryPathOverride,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var workspacePath = GetWorkspacePath(agentName);
        var (memoryRoot, defaultTargetPath) = ResolveMemoryRoot(workspacePath, memoryPathOverride);
        var targetPath = ResolveMemoryPath(memoryRoot, defaultTargetPath, filePath);
        var targetDirectory = _fileSystem.Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
            _fileSystem.Directory.CreateDirectory(targetDirectory);

        var memoryEntry = content.EndsWith(Environment.NewLine, StringComparison.Ordinal)
            ? content
            : $"{content}{Environment.NewLine}";

        await _fileSystem.File.AppendAllTextAsync(targetPath, memoryEntry, cancellationToken);
    }

    public string GetWorkspacePath(string agentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        return Path.Combine(_botNexusHome.GetAgentDirectory(agentName.Trim()), "workspace");
    }

    private async Task<string> ReadFileOrEmptyAsync(string path, CancellationToken cancellationToken)
    {
        if (!_fileSystem.File.Exists(path))
            return string.Empty;

        return await _fileSystem.File.ReadAllTextAsync(path, cancellationToken);
    }

    private (string MemoryRoot, string? DefaultTargetPath) ResolveMemoryRoot(string workspacePath, string? memoryPathOverride)
    {
        var workspaceFullPath = _fileSystem.Path.GetFullPath(workspacePath);
        var relativePath = string.IsNullOrWhiteSpace(memoryPathOverride)
            ? MemoryDirectoryName
            : memoryPathOverride.Trim().Replace('\\', '/');
        if (_fileSystem.Path.IsPathRooted(relativePath))
            throw new ArgumentException("memory.path must be workspace-relative.", nameof(memoryPathOverride));

        var overrideFullPath = _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(workspaceFullPath, relativePath));
        EnsureWithinRoot(workspaceFullPath, overrideFullPath, nameof(memoryPathOverride), "memory.path must remain within the workspace.");

        if (overrideFullPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var fileDirectory = _fileSystem.Path.GetDirectoryName(overrideFullPath);
            var memoryRoot = string.IsNullOrWhiteSpace(fileDirectory)
                ? _fileSystem.Path.Combine(workspaceFullPath, MemoryDirectoryName)
                : fileDirectory;
            return (memoryRoot, overrideFullPath);
        }

        return (overrideFullPath, defaultTargetPath: null);
    }

    private string ResolveMemoryPath(string memoryRoot, string? defaultTargetPath, string? filePath)
    {
        _fileSystem.Directory.CreateDirectory(memoryRoot);
        if (string.IsNullOrWhiteSpace(filePath))
            return defaultTargetPath ?? _fileSystem.Path.Combine(memoryRoot, $"{DateTime.UtcNow:yyyy-MM-dd}.md");

        if (_fileSystem.Path.IsPathRooted(filePath))
            throw new ArgumentException("file_path must be relative to the memory root.", nameof(filePath));

        var normalized = filePath.Trim();
        if (normalized.StartsWith($"{MemoryDirectoryName}/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith($"{MemoryDirectoryName}\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(MemoryDirectoryName.Length + 1);
        }

        var resolved = _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(memoryRoot, normalized));
        EnsureWithinRoot(memoryRoot, resolved, nameof(filePath), "file_path must remain within the memory root.");
        return resolved;
    }

    private void EnsureWithinRoot(string root, string path, string parameterName, string message)
    {
        var prefix = root.TrimEnd(_fileSystem.Path.DirectorySeparatorChar, _fileSystem.Path.AltDirectorySeparatorChar)
            + _fileSystem.Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            !path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(message, parameterName);
        }
    }
}

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

        var recentNotes = await LoadRecentDailyNotesAsync(workspacePath, cancellationToken);
        return new AgentWorkspace(agentName.Trim(), soul, identity, user, memory, recentNotes);
    }

    public async Task SaveMemoryAsync(string agentName, string content, CancellationToken cancellationToken = default)
        => await SaveMemoryAsync(agentName, filePath: null, content, cancellationToken);

    public async Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var workspacePath = GetWorkspacePath(agentName);
        var memoryRoot = _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(workspacePath, MemoryDirectoryName));
        var targetPath = ResolveMemoryPath(memoryRoot, filePath);
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

    private async Task<IReadOnlyList<DailyMemoryNote>> LoadRecentDailyNotesAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var memoryRoot = _fileSystem.Path.Combine(workspacePath, MemoryDirectoryName);
        if (!_fileSystem.Directory.Exists(memoryRoot))
            return [];

        var today = DateTime.Now.Date;
        var targetDates = new HashSet<string>(StringComparer.Ordinal)
        {
            today.ToString("yyyy-MM-dd"),
            today.AddDays(-1).ToString("yyyy-MM-dd")
        };

        var candidates = _fileSystem.Directory.GetFiles(memoryRoot, "*.md")
            .Select(path => new
            {
                FullPath = path,
                FileName = _fileSystem.Path.GetFileNameWithoutExtension(path),
                DisplayPath = $"memory/{_fileSystem.Path.GetFileName(path)}"
            })
            .Where(x => targetDates.Contains(x.FileName))
            .OrderByDescending(x => x.FileName, StringComparer.Ordinal)
            .ToList();

        List<DailyMemoryNote> notes = [];
        foreach (var candidate in candidates)
        {
            var content = await _fileSystem.File.ReadAllTextAsync(candidate.FullPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
                notes.Add(new DailyMemoryNote(candidate.DisplayPath, content.Trim()));
        }

        return notes;
    }

    private string ResolveMemoryPath(string memoryRoot, string? filePath)
    {
        _fileSystem.Directory.CreateDirectory(memoryRoot);
        if (string.IsNullOrWhiteSpace(filePath))
            return _fileSystem.Path.Combine(memoryRoot, $"{DateTime.Now:yyyy-MM-dd}.md");

        if (_fileSystem.Path.IsPathRooted(filePath))
            throw new ArgumentException("file_path must be relative to the memory root.", nameof(filePath));

        var normalized = filePath.Trim();
        if (normalized.StartsWith($"{MemoryDirectoryName}/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith($"{MemoryDirectoryName}\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(MemoryDirectoryName.Length + 1);
        }

        var resolved = _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(memoryRoot, normalized));
        var memoryPrefix = memoryRoot.TrimEnd(_fileSystem.Path.DirectorySeparatorChar, _fileSystem.Path.AltDirectorySeparatorChar)
            + _fileSystem.Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(memoryPrefix, StringComparison.OrdinalIgnoreCase) &&
            !resolved.Equals(memoryRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("file_path must remain within the memory root.", nameof(filePath));

        return resolved;
    }
}
